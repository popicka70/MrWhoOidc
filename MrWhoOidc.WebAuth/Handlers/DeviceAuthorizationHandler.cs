using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Extensions;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.WebAuth.Handlers;

/// <summary>
/// Handles the Device Authorization endpoint (RFC 8628).
/// Devices call this endpoint to request authorization and receive a device_code + user_code.
/// </summary>
public interface IDeviceAuthorizationHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class DeviceAuthorizationHandler(
    OidcOptions oidcOptions,
    IOptions<AuthOptions> authOptions,
    AuthDbContext db,
    IClientStore clients,
    IClientAssertionValidator assertions,
    ITenantAccessor tenantAccessor,
    ILogger<DeviceAuthorizationHandler> logger) : IDeviceAuthorizationHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var corr = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        var options = authOptions.Value;

        // Must be POST with form content
        if (!http.Request.HasFormContentType)
        {
            return DeviceAuthorizationError(OAuthConstants.ErrorCodes.InvalidRequest, "Form content expected", corr);
        }

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;

        // Client authentication (public clients are allowed per RFC 8628)
        var (clientId, clientSecretFromHeader) = ReadClientCredentials(http);
        if (string.IsNullOrEmpty(clientId)) clientId = form[OAuthConstants.Parameters.ClientId].ToString();

        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogWarning("[DeviceAuth] Missing client_id corr={Corr}", corr);
            return DeviceAuthorizationError(OAuthConstants.ErrorCodes.InvalidRequest, "Missing client_id", corr);
        }

        // Verify client exists and is allowed to use device flow
        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == clientId && c.TenantId == tenantId, http.RequestAborted);

        if (client == null)
        {
            logger.LogWarning("[DeviceAuth] Unknown client corr={Corr} client={ClientId}", corr, clientId);
            return DeviceAuthorizationError(OAuthConstants.ErrorCodes.InvalidClient, "Unknown client", corr);
        }

        // Authenticate confidential clients
        bool isConfidentialClient = client.ClientSecrets.Any();
        if (isConfidentialClient)
        {
            var authenticated = await AuthenticateClientAsync(http, form, clientId, clientSecretFromHeader);
            if (!authenticated)
            {
                logger.LogWarning("[DeviceAuth] Client authentication failed corr={Corr} client={ClientId}", corr, clientId);
                return DeviceAuthorizationError(OAuthConstants.ErrorCodes.InvalidClient, "Client authentication failed", corr);
            }
        }

        // Parse requested scopes
        var scopeParam = form[OAuthConstants.Parameters.Scope].ToString();
        var requestedScopes = string.IsNullOrWhiteSpace(scopeParam)
            ? Array.Empty<string>()
            : scopeParam.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Optional resource/audience
        var resource = form[OAuthConstants.Parameters.Resource].ToString();
        var audience = form[OAuthConstants.Parameters.Audience].ToString();

        // Generate device_code and user_code
        var deviceCode = GenerateDeviceCode();
        var userCode = GenerateUserCode(options.DeviceCodeUserCodeCharset, options.DeviceCodeUserCodeLength);

        // Calculate expiration
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(options.DeviceCodeLifetimeSeconds);

        // Store in database
        var entry = new DeviceCodeEntry
        {
            TenantId = tenantId,
            DeviceCode = deviceCode,
            UserCode = userCode,
            ClientId = clientId,
            ScopesJson = System.Text.Json.JsonSerializer.Serialize(requestedScopes),
            Resource = !string.IsNullOrEmpty(resource) ? resource : audience,
            Status = DeviceCodeStatus.Pending,
            ExpiresAt = expiresAt,
            IntervalSeconds = options.DeviceCodePollingIntervalSeconds,
            DeviceIpAddress = http.Connection.RemoteIpAddress?.ToString(),
            DeviceUserAgent = http.Request.Headers.UserAgent.ToString()
        };

        db.DeviceCodes.Add(entry);
        await db.SaveChangesAsync(http.RequestAborted);

        // Log only the client id and a hash of the user_code. The plaintext user_code is a bearer
        // credential (it is what the user enters at the verification page to approve the device),
        // so it must never be written to logs.
        logger.LogInformation("[DeviceAuth] Device authorization requested for client {ClientId} userCodeHash={UserCodeHash}",
            clientId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userCode)))[..16]);

        // Build response per RFC 8628
        var issuer = http.GetIssuer(oidcOptions);
        var verificationUri = $"{issuer}/device";
        // Use the formatted (display-friendly) user_code in both the response field
        // and the verification URI, so the URL contains the same value the user is shown.
        var formattedUserCode = FormatUserCode(userCode);
        var verificationUriComplete = $"{verificationUri}?user_code={Uri.EscapeDataString(formattedUserCode)}";

        var response = new Dictionary<string, object>
        {
            ["device_code"] = deviceCode,
            ["user_code"] = formattedUserCode,
            ["verification_uri"] = verificationUri,
            ["verification_uri_complete"] = verificationUriComplete,
            ["expires_in"] = options.DeviceCodeLifetimeSeconds,
            ["interval"] = options.DeviceCodePollingIntervalSeconds
        };

        // RFC 8628: The response uses application/json
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
            var deviceEndpoint = http.GetIssuer(oidcOptions) + "/device";
            return await assertions.ValidateAsync(clientId, clientAssertion, deviceEndpoint).ConfigureAwait(false);
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
        return MrWhoOidc.WebAuth.Infrastructure.BasicClientCredentialsParser.ReadFromAuthorizationHeader(http.Request.Headers.Authorization.FirstOrDefault());
    }

    private static string GenerateDeviceCode()
    {
        // RFC 8628 recommends at least 160 bits of entropy
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static string GenerateUserCode(string charset, int length)
    {
        if (string.IsNullOrEmpty(charset)) charset = "BCDFGHJKLMNPQRSTVWXZ";
        if (length <= 0) length = 8;

        var sb = new StringBuilder(length);
        Span<byte> buffer = stackalloc byte[length];
        RandomNumberGenerator.Fill(buffer);

        for (int i = 0; i < length; i++)
        {
            sb.Append(charset[buffer[i] % charset.Length]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Format user code for display (e.g., XXXX-XXXX)
    /// </summary>
    private static string FormatUserCode(string code)
    {
        if (code.Length == 8)
        {
            return $"{code[..4]}-{code[4..]}";
        }
        return code;
    }

    private static IResult DeviceAuthorizationError(string error, string? description, string? correlationId = null)
    {
        var body = new Dictionary<string, object>
        {
            ["error"] = error
        };

        if (!string.IsNullOrEmpty(description))
        {
            body["error_description"] = description;
        }

        if (!string.IsNullOrEmpty(correlationId))
        {
            body["correlation_id"] = correlationId;
        }

        return Results.Json(body, statusCode: StatusCodes.Status400BadRequest);
    }
}
