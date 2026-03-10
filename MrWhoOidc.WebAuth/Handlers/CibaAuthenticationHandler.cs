using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Extensions;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Linq;

namespace MrWhoOidc.WebAuth.Handlers;

/// <summary>
/// Handles the Backchannel Authentication endpoint (OpenID Connect CIBA Core 1.0).
/// Clients call this endpoint to initiate authentication of an end-user.
/// </summary>
public interface ICibaAuthenticationHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class CibaAuthenticationHandler(
    OidcOptions oidcOptions,
    IOptions<AuthOptions> authOptions,
    AuthDbContext db,
    IClientStore clients,
    IClientAssertionValidator assertions,
    ITokenValidator tokenValidator,
    ITenantAccessor tenantAccessor,
    ICibaNotificationService notificationService,
    ILogger<CibaAuthenticationHandler> logger,
    IHttpClientFactory? httpClientFactory = null,
    IJwksCache? jwksCache = null) : ICibaAuthenticationHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var corr = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        var options = authOptions.Value;

        // Feature check
        if (!options.EnableCiba)
        {
            return Results.NotFound();
        }

        // Must be POST with form content
        if (!http.Request.HasFormContentType)
        {
            return CibaError(OAuthConstants.ErrorCodes.InvalidRequest, "Form content expected", corr);
        }

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;
        var issuer = http.GetIssuer(oidcOptions);

        // === Client Authentication (REQUIRED for CIBA per spec) ===
        var (clientId, clientSecretFromHeader) = ReadClientCredentials(http);
        if (string.IsNullOrEmpty(clientId)) clientId = form[OAuthConstants.Parameters.ClientId].ToString();

        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("[CIBA] Missing client_id corr={Corr}", corr);
            return CibaError(OAuthConstants.ErrorCodes.InvalidRequest, "Missing client_id", corr);
        }

        // Verify client exists
        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == clientId && c.TenantId == tenantId, http.RequestAborted);

        if (client == null)
        {
            logger.LogWarning("[CIBA] Unknown client corr={Corr} client={ClientId}", corr, clientId);
            return CibaError(OAuthConstants.ErrorCodes.InvalidClient, "Unknown client", corr);
        }

        // CIBA requires client authentication (confidential clients only)
        var authenticated = await AuthenticateClientAsync(http, form, clientId, clientSecretFromHeader);
        if (!authenticated)
        {
            logger.LogWarning("[CIBA] Client authentication failed corr={Corr} client={ClientId}", corr, clientId);
            return CibaError(OAuthConstants.ErrorCodes.InvalidClient, "Client authentication failed", corr);
        }

        // === User Identification (ONE of login_hint, login_hint_token, id_token_hint REQUIRED) ===
        var loginHint = form[OAuthConstants.Parameters.LoginHint].ToString();
        var loginHintToken = form[OAuthConstants.Parameters.LoginHintToken].ToString();
        var idTokenHint = form[OAuthConstants.Parameters.IdTokenHint].ToString();

        string? userIdentifierHint = null;
        string? hintType = null;

        int hintCount = (string.IsNullOrWhiteSpace(loginHint) ? 0 : 1)
                      + (string.IsNullOrWhiteSpace(loginHintToken) ? 0 : 1)
                      + (string.IsNullOrWhiteSpace(idTokenHint) ? 0 : 1);

        if (hintCount == 0)
        {
            logger.LogWarning("[CIBA] No user hint provided corr={Corr} client={ClientId}", corr, clientId);
            return CibaError(OAuthConstants.ErrorCodes.InvalidRequest,
                "One of login_hint, login_hint_token, or id_token_hint is required", corr);
        }

        if (hintCount > 1)
        {
            logger.LogWarning("[CIBA] Multiple user hints provided corr={Corr} client={ClientId}", corr, clientId);
            return CibaError(OAuthConstants.ErrorCodes.InvalidRequest,
                "Only one of login_hint, login_hint_token, or id_token_hint should be provided", corr);
        }

        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            userIdentifierHint = loginHint;
            hintType = "login_hint";
        }
        else if (!string.IsNullOrWhiteSpace(loginHintToken))
        {
            // login_hint_token would be a signed JWT containing user info - validate and extract subject
            var subject = await ValidateLoginHintTokenAsync(loginHintToken, client, issuer, http.RequestAborted);
            if (subject == null)
            {
                return CibaError(OAuthConstants.ErrorCodes.InvalidRequest, "Invalid login_hint_token", corr);
            }
            userIdentifierHint = subject;
            hintType = "login_hint_token";
        }
        else if (!string.IsNullOrWhiteSpace(idTokenHint))
        {
            // id_token_hint must be a valid OP-issued ID token and include a subject.
            var subject = await ExtractSubjectFromIdTokenAsync(idTokenHint, issuer, http.RequestAborted);
            if (subject == null)
            {
                return CibaError(OAuthConstants.ErrorCodes.InvalidRequest, "Invalid id_token_hint", corr);
            }
            userIdentifierHint = subject;
            hintType = "id_token_hint";
        }

        // === Optional Parameters ===
        var scopeParam = form[OAuthConstants.Parameters.Scope].ToString();
        var requestedScopes = string.IsNullOrWhiteSpace(scopeParam)
            ? ["openid"]
            : scopeParam.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // CIBA requires openid scope
        if (!requestedScopes.Contains("openid", StringComparer.OrdinalIgnoreCase))
        {
            return CibaError(OAuthConstants.ErrorCodes.InvalidScope, "openid scope is required for CIBA", corr);
        }

        var bindingMessage = form[OAuthConstants.Parameters.BindingMessage].ToString();
        if (!string.IsNullOrEmpty(bindingMessage) && bindingMessage.Length > 200)
        {
            return CibaError(OAuthConstants.ErrorCodes.InvalidBindingMessage, "binding_message too long", corr);
        }

        var userCode = form[OAuthConstants.Parameters.UserCode].ToString();
        if (!string.IsNullOrEmpty(userCode))
        {
            if (!options.CibaUserCodeParameterSupported)
            {
                return CibaError(OAuthConstants.ErrorCodes.InvalidRequest, "user_code parameter not supported", corr);
            }
            // Validate user_code format
            if (userCode.Length > 20)
            {
                return CibaError(OAuthConstants.ErrorCodes.InvalidUserCode, "user_code too long", corr);
            }
        }

        var clientNotificationToken = form[OAuthConstants.Parameters.ClientNotificationToken].ToString();
        var deliveryMode = DetermineDeliveryMode(options.CibaTokenDeliveryModesSupported, clientNotificationToken);
        if ((string.Equals(deliveryMode, "ping", StringComparison.OrdinalIgnoreCase)
                || string.Equals(deliveryMode, "push", StringComparison.OrdinalIgnoreCase))
            && string.IsNullOrWhiteSpace(clientNotificationToken))
        {
            return CibaError(OAuthConstants.ErrorCodes.InvalidRequest,
                "client_notification_token is required for selected token delivery mode", corr);
        }

        if (!string.IsNullOrWhiteSpace(clientNotificationToken) && !IsValidClientNotificationToken(clientNotificationToken))
        {
            return CibaError(OAuthConstants.ErrorCodes.InvalidRequest, "Invalid client_notification_token", corr);
        }

        var acrValues = form[OAuthConstants.Parameters.AcrValues].ToString();
        var resource = form[OAuthConstants.Parameters.Resource].ToString();
        var audience = form[OAuthConstants.Parameters.Audience].ToString();

        // requested_expiry is optional (default to server config)
        int? requestedExpiry = null;
        var requestedExpiryStr = form[OAuthConstants.Parameters.RequestedExpiry].ToString();
        if (!string.IsNullOrEmpty(requestedExpiryStr) && int.TryParse(requestedExpiryStr, out var re))
        {
            requestedExpiry = re;
        }

        // Calculate expiration
        var expiresInSeconds = requestedExpiry ?? options.CibaAuthRequestLifetimeSeconds;
        // Cap at max lifetime
        if (expiresInSeconds > options.CibaAuthRequestLifetimeSeconds)
        {
            expiresInSeconds = options.CibaAuthRequestLifetimeSeconds;
        }
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);

        // Generate auth_req_id
        var authReqId = GenerateAuthReqId();

        // Store CIBA request
        var entry = new CibaAuthenticationRequest
        {
            TenantId = tenantId,
            AuthReqId = authReqId,
            ClientId = clientId,
            UserIdentifierHint = userIdentifierHint,
            HintType = hintType,
            ScopesJson = System.Text.Json.JsonSerializer.Serialize(requestedScopes),
            BindingMessage = string.IsNullOrEmpty(bindingMessage) ? null : bindingMessage,
            UserCode = string.IsNullOrEmpty(userCode) ? null : userCode,
            AcrValues = string.IsNullOrEmpty(acrValues) ? null : acrValues,
            ClientNotificationToken = string.IsNullOrEmpty(clientNotificationToken) ? null : clientNotificationToken,
            Resource = !string.IsNullOrEmpty(resource) ? resource : (!string.IsNullOrEmpty(audience) ? audience : null),
            Status = CibaRequestStatus.Pending,
            ExpiresAt = expiresAt,
            IntervalSeconds = options.CibaPollingIntervalSeconds,
            ClientIpAddress = http.Connection.RemoteIpAddress?.ToString(),
            RequestedExpiresIn = requestedExpiry
        };

        db.CibaAuthenticationRequests.Add(entry);
        await db.SaveChangesAsync(http.RequestAborted);

        logger.LogInformation("[CIBA] Backchannel authentication initiated corr={Corr} client={ClientId} authReqId={AuthReqId} hint={HintType}",
            corr, clientId, authReqId, hintType);

        // Trigger notification to user (implementation-specific)
        // This could be push notification, SMS, email, etc.
        try
        {
            await notificationService.NotifyUserAsync(entry, http.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CIBA] Failed to send user notification corr={Corr} authReqId={AuthReqId}", corr, authReqId);
            // Don't fail the request - the user can still be notified through other means
        }

        // Build response per CIBA spec
        var response = new Dictionary<string, object>
        {
            ["auth_req_id"] = authReqId,
            ["expires_in"] = expiresInSeconds,
            ["interval"] = options.CibaPollingIntervalSeconds
        };

        http.Response.Headers["Cache-Control"] = "no-store";
        http.Response.Headers["Pragma"] = "no-cache";

        return Results.Json(response, statusCode: StatusCodes.Status200OK);
    }

    private async Task<bool> AuthenticateClientAsync(HttpContext http, IFormCollection form, string clientId, string? clientSecretFromHeader)
    {
        var clientAssertionType = form[OAuthConstants.Parameters.ClientAssertionType].ToString();
        var clientAssertion = form[OAuthConstants.Parameters.ClientAssertion].ToString();

        if (string.Equals(clientAssertionType, OAuthConstants.ClientAssertionTypes.JwtBearer, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(clientAssertion))
        {
            var cibaEndpoint = http.GetIssuer(oidcOptions) + "/bc-authorize";
            return await assertions.ValidateAsync(clientId, clientAssertion, cibaEndpoint).ConfigureAwait(false);
        }

        // Secret-based auth
        var clientSecret = clientSecretFromHeader;
        if (string.IsNullOrEmpty(clientSecret))
        {
            clientSecret = form[OAuthConstants.Parameters.ClientSecret].ToString();
        }

        return await clients.ValidateClientSecretAsync(clientId, clientSecret).ConfigureAwait(false);
    }

    private static (string? clientId, string? clientSecret) ReadClientCredentials(HttpContext http)
    {
        var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !AuthenticationHeaderValue.TryParse(authHeader, out var parsed))
        {
            return (null, null);
        }

        if (!string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(parsed.Parameter))
        {
            return (null, null);
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
            var idx = decoded.IndexOf(':');
            if (idx < 0) return (null, null);
            return (Uri.UnescapeDataString(decoded[..idx]), Uri.UnescapeDataString(decoded[(idx + 1)..]));
        }
        catch
        {
            return (null, null);
        }
    }

    private static string GenerateAuthReqId()
    {
        // CIBA spec recommends sufficient entropy (similar to authorization codes)
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static string DetermineDeliveryMode(string[]? configuredModes, string? clientNotificationToken)
    {
        var modes = (configuredModes ?? Array.Empty<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim().ToLowerInvariant())
            .ToArray();

        if (modes.Length == 0)
        {
            return "poll";
        }

        if (modes.Length == 1)
        {
            return modes[0];
        }

        if (!string.IsNullOrWhiteSpace(clientNotificationToken))
        {
            if (modes.Contains("ping", StringComparer.Ordinal)) return "ping";
            if (modes.Contains("push", StringComparer.Ordinal)) return "push";
        }

        return modes.Contains("poll", StringComparer.Ordinal) ? "poll" : modes[0];
    }

    private static bool IsValidClientNotificationToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024)
        {
            return false;
        }

        for (var i = 0; i < token.Length; i++)
        {
            if (char.IsControl(token[i]) || char.IsWhiteSpace(token[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TokenHasExpectedAudience(JwtSecurityToken jwt, string expectedAudience)
    {
        if (string.IsNullOrWhiteSpace(expectedAudience))
        {
            return true;
        }

        var audiences = jwt.Audiences?.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray() ?? Array.Empty<string>();
        if (audiences.Length == 0)
        {
            // Require an explicit audience claim targeted at this OP.
            return false;
        }

        return audiences.Contains(expectedAudience, StringComparer.Ordinal);
    }

    private async Task<string?> ValidateLoginHintTokenAsync(string token, Client client, string issuer, CancellationToken ct)
    {
        // login_hint_token must be a signed JWT from the client and targeted at this OP.
        try
        {
            var keys = await ClientJwksResolver.GetSigningKeysAsync(
                client,
                httpClientFactory,
                jwksCache,
                authOptions.Value.ClientJwksCacheSeconds,
                ct).ConfigureAwait(false);
            if (keys.Count == 0)
            {
                logger.LogWarning("[CIBA] login_hint_token rejected: client has no signing keys configured client={ClientId}", client.ClientId);
                return null;
            }

            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            if (!handler.CanReadToken(token))
            {
                return null;
            }

            var jwt = handler.ReadJwtToken(token);
            if (!TokenHasExpectedAudience(jwt, issuer))
            {
                logger.LogWarning("[CIBA] login_hint_token rejected: audience mismatch client={ClientId}", client.ClientId);
                return null;
            }

            var tvp = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = client.ClientId,
                ValidateAudience = true,
                ValidAudience = issuer,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = keys,
                NameClaimType = "sub",
                RoleClaimType = "role"
            };

            var principal = handler.ValidateToken(token, tvp, out _);
            var subject = principal.FindFirstValue("sub");
            return string.IsNullOrWhiteSpace(subject) ? null : subject;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> ExtractSubjectFromIdTokenAsync(string idToken, string issuer, CancellationToken ct)
    {
        var (ok, principal, _) = await tokenValidator.ValidateAsync(idToken, issuer, ct).ConfigureAwait(false);
        if (!ok || principal == null)
        {
            return null;
        }

        var subject = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        return subject;
    }

    private static IResult CibaError(string error, string description, string correlationId)
    {
        return Results.Json(new Dictionary<string, object>
        {
            ["error"] = error,
            ["error_description"] = description
        }, statusCode: StatusCodes.Status400BadRequest);
    }
}
