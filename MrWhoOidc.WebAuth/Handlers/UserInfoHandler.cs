using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Protocols;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Infrastructure.Logging;
using MrWhoOidc.WebAuth.Observability;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MrWhoOidc.Security;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IUserInfoHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class UserInfoHandler(
    OidcOptions options,
    IOptions<AuthOptions> authOptions,
    ITokenValidator validator,
    IJwtService jwt,
    OidcEndpointMetrics metrics,
    IDPoPValidator dpop,
    IDPoPReplayCache replayCache,
    IDPoPNonceStore nonceStore,
    ILogger<UserInfoHandler> logger,
    AuthDbContext db,
    IHttpClientFactory? httpClientFactory = null,
    IJwksCache? jwksCache = null) : IUserInfoHandler
{
    private sealed record ClaimConstraint(bool Essential, string? Value, string[]? Values);
    private sealed record UserInfoDbData(string? Username, string? Name, string? Email, bool? EmailVerified);

    private static readonly JsonSerializerOptions EmbeddedClaimsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var sw = Stopwatch.StartNew();
        string outcome = "success";
        try
        {
            metrics.UserInfoRequests.Add(1);
            var auth = http.Request.Headers.Authorization.ToString();
            var bearerPrefix = OAuthConstants.TokenTypes.Bearer + " ";
            if (!string.IsNullOrEmpty(auth) && !auth.StartsWith(bearerPrefix, StringComparison.Ordinal))
            {
                outcome = "failure";
                logger.LogWarning("/userinfo 401: missing or invalid Authorization header from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(ErrorResults.InvalidToken());
            }

            var headerToken = string.IsNullOrEmpty(auth)
                ? null
                : auth.Substring(bearerPrefix.Length).Trim();
            string? bodyToken = null;

            if (HttpMethods.IsPost(http.Request.Method) && http.Request.HasFormContentType)
            {
                var form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
                bodyToken = form[OAuthConstants.Parameters.AccessToken].ToString();
            }

            if (!string.IsNullOrEmpty(headerToken) && !string.IsNullOrEmpty(bodyToken))
            {
                outcome = "failure";
                logger.LogWarning("/userinfo 400: multiple bearer token transports from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(
                    ErrorResults.InvalidRequest("Multiple bearer token transports are not allowed."),
                    "invalid_request");
            }

            var token = headerToken ?? bodyToken;
            if (string.IsNullOrEmpty(token))
            {
                outcome = "failure";
                logger.LogWarning("/userinfo 401: missing bearer token from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(ErrorResults.InvalidToken());
            }

            var issuer = http.GetIssuer(options);

            // Require typ=at+jwt to avoid accepting other JWT types (e.g., id_token).
            try
            {
                var unsigned = new JwtSecurityTokenHandler().ReadJwtToken(token);
                if (!string.Equals(unsigned.Header.Typ, SecurityConstants.JwtTokenTypes.AtJwt, StringComparison.OrdinalIgnoreCase))
                {
                    outcome = "failure";
                    logger.LogWarning("/userinfo 401: invalid typ={Typ} from {IP}", unsigned.Header.Typ ?? "(null)", http.Connection.RemoteIpAddress?.ToString());
                    metrics.UserInfoFailures.Add(1);
                    return WithWwwAuthenticate(ErrorResults.InvalidToken());
                }
            }
            catch
            {
                outcome = "failure";
                logger.LogWarning("/userinfo 401: token not parseable as JWT from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(ErrorResults.InvalidToken());
            }

            var (ok, principal, _) = await validator.ValidateAsync(token, issuer).ConfigureAwait(false);
            if (!ok || principal is not { })
            {
                outcome = "failure";
                logger.LogWarning("/userinfo 401: token validation failed from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(ErrorResults.InvalidToken());
            }

            // Require scope claim to distinguish access tokens from ID tokens.
            // (This server's access tokens always include OAuth 'scope'; ID tokens do not.)
            var scopeClaim = principal.FindFirst("scope")?.Value;
            if (string.IsNullOrWhiteSpace(scopeClaim))
            {
                outcome = "failure";
                logger.LogWarning("/userinfo 401: missing scope claim from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(ErrorResults.InvalidToken());
            }

            // Audience hardening: require at least one audience and enforce a conservative allow policy.
            // Allow if:
            // - audience matches configured ApiAudiences, OR
            // - audience is an absolute URI and its host matches the issuer host.
            // This reduces cross-audience acceptance while keeping common deployments working.
            var audiences = principal.Claims.Where(c => c.Type == "aud").Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToArray();
            if (audiences.Length == 0)
            {
                outcome = "failure";
                logger.LogWarning("/userinfo 401: missing aud claim from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(ErrorResults.InvalidToken());
            }

            var allowedApiAudiences = authOptions.Value.ApiAudiences ?? Array.Empty<string>();
            Uri? issuerUri = null;
            _ = Uri.TryCreate(issuer, UriKind.Absolute, out issuerUri);

            var azp = principal.FindFirst("azp")?.Value;

            var audienceAllowed = audiences.Any(a => allowedApiAudiences.Contains(a, StringComparer.Ordinal)) ||
                                 (!string.IsNullOrEmpty(azp) && audiences.Any(a => string.Equals(a, azp, StringComparison.Ordinal))) ||
                                 (issuerUri is not null && audiences.Any(a => Uri.TryCreate(a, UriKind.Absolute, out var audUri) && string.Equals(audUri.Host, issuerUri.Host, StringComparison.OrdinalIgnoreCase)));

            if (!audienceAllowed)
            {
                outcome = "failure";
                logger.LogWarning("/userinfo 401: audience not allowed from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(ErrorResults.InvalidToken());
            }

            // If token is DPoP-bound (has cnf.jkt), require and validate DPoP proof
            string? cnfJkt = null;
            var cnfRaw = principal!.FindFirst("cnf")?.Value;
            if (!string.IsNullOrEmpty(cnfRaw))
            {
                try
                {
                    using var cnfDoc = System.Text.Json.JsonDocument.Parse(cnfRaw);
                    if (cnfDoc.RootElement.TryGetProperty("jkt", out var jktProp))
                    {
                        cnfJkt = jktProp.GetString();
                    }
                }
                catch
                {
                    // ignore parse errors, will be treated as missing jkt below
                }

                if (string.IsNullOrEmpty(cnfJkt))
                {
                    outcome = "failure";
                    logger.LogWarning("/userinfo 401: cnf claim present without jkt from {IP}", http.Connection.RemoteIpAddress?.ToString());
                    metrics.UserInfoFailures.Add(1);
                    return WithWwwAuthenticate(ErrorResults.InvalidToken());
                }

                // Use actual request URL for DPoP validation (what client sees), not PublicBaseUrl
                var endpointUrl = http.GetEndpointUrl();
                var validation = await dpop.ValidateForEndpointAsync(http, endpointUrl, token).ConfigureAwait(false);

                var clientIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (!validation.Ok)
                {
                    outcome = "failure";
                    logger.LogWarning("/userinfo 401: invalid DPoP proof reason={Reason} from {IP}", validation.Error ?? "unknown", clientIp);
                    metrics.UserInfoFailures.Add(1);
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                    return ErrorResults.InvalidToken();
                }

                if (string.IsNullOrEmpty(validation.Jkt) || !string.Equals(validation.Jkt, cnfJkt, StringComparison.Ordinal))
                {
                    outcome = "failure";
                    logger.LogWarning("/userinfo 401: cnf.jkt mismatch from {IP}", clientIp);
                    metrics.UserInfoFailures.Add(1);
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                    return ErrorResults.InvalidToken();
                }

                // Nonce challenge support (only after proof is valid and matches token binding)
                (bool nonceOk, string serverNonce) = await nonceStore
                    .ValidateOrIssueAsync(endpointUrl, clientIp, validation.Jkt, validation.Nonce)
                    .ConfigureAwait(false);
                if (!nonceOk)
                {
                    http.Response.Headers["DPoP-Nonce"] = serverNonce;
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=use_dpop_nonce";
                    logger.LogInformation("/userinfo nonce challenge issued to {IP}", clientIp);
                    return Results.Unauthorized();
                }

                // Replay protection: DPoP jti must not repeat within window
                if (string.IsNullOrEmpty(validation.Jti) || validation.Iat is null)
                {
                    outcome = "failure";
                    logger.LogWarning("/userinfo 401: DPoP missing jti/iat from {IP}", clientIp);
                    metrics.UserInfoFailures.Add(1);
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                    return ErrorResults.InvalidToken();
                }
                var key = $"{validation.Jkt}:{validation.Jti}";
                var expires = DateTimeOffset.FromUnixTimeSeconds(validation.Iat.Value).AddMinutes(5);
                if (!replayCache.TryAdd(key, expires))
                {
                    outcome = "failure";
                    logger.LogWarning("/userinfo 401: DPoP replay detected for {Key} from {IP}", key, clientIp);
                    metrics.UserInfoFailures.Add(1);
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=replay";
                    return ErrorResults.InvalidToken();
                }
            }

            var scopes = (scopeClaim ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var sub = principal.FindFirstValue("sub");

            // OIDC claims parameter support (best-effort): if the access token carries an embedded
            // requested userinfo claims list, we filter the response down to those claims.
            HashSet<string>? requestedUserInfoClaims = null;
            var requestedUserInfoClaimsJson = principal.FindFirst("mrwho_userinfo_claims")?.Value;
            if (!string.IsNullOrWhiteSpace(requestedUserInfoClaimsJson))
            {
                try
                {
                    var arr = JsonSerializer.Deserialize<string[]>(requestedUserInfoClaimsJson);
                    if (arr is { Length: > 0 })
                    {
                        requestedUserInfoClaims = arr
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => s.Trim())
                            .ToHashSet(StringComparer.Ordinal);
                    }
                }
                catch
                {
                    // ignore invalid embedded value
                }
            }

            // Also honor embedded constraints for userinfo claims.
            Dictionary<string, ClaimConstraint>? requestedUserInfoConstraints = null;
            var requestedUserInfoConstraintsJson = principal.FindFirst("mrwho_userinfo_claims_constraints")?.Value;
            if (!string.IsNullOrWhiteSpace(requestedUserInfoConstraintsJson))
            {
                try
                {
                    requestedUserInfoConstraints = JsonSerializer.Deserialize<Dictionary<string, ClaimConstraint>>(requestedUserInfoConstraintsJson, EmbeddedClaimsJsonOptions);
                }
                catch
                {
                    // ignore invalid embedded value
                }
            }

            static bool WantsClaim(string claimName, HashSet<string>? requestedClaims, Dictionary<string, ClaimConstraint>? requestedConstraints)
                => (requestedClaims?.Contains(claimName) ?? false) || (requestedConstraints?.ContainsKey(claimName) ?? false);

            var wantsName = scopes.Contains(OidcConstants.Scopes.Profile)
                || WantsClaim(OidcConstants.Claims.Name, requestedUserInfoClaims, requestedUserInfoConstraints);

            var wantsEmailClaims = scopes.Contains(OidcConstants.Scopes.Email)
                || WantsClaim(OidcConstants.Claims.Email, requestedUserInfoClaims, requestedUserInfoConstraints)
                || WantsClaim(OidcConstants.Claims.EmailVerified, requestedUserInfoClaims, requestedUserInfoConstraints)
                || WantsClaim("emails", requestedUserInfoClaims, requestedUserInfoConstraints);

            // Resolve user data from DB when the token does not carry profile/email claims.
            // This keeps access tokens lean while allowing /userinfo to return scoped claims and
            // claims requested explicitly via the OIDC claims parameter.
            UserInfoDbData? userData = null;
            if (!string.IsNullOrWhiteSpace(sub) &&
                Guid.TryParse(sub, out var lookupUserId) &&
                (wantsName || wantsEmailClaims))
            {
                userData = await db.Users.AsNoTracking()
                    .Where(u => u.Id == lookupUserId)
                    .Select(u => new UserInfoDbData(u.Username, u.Name, u.Email, u.EmailVerified))
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);
            }

            var payload = new Dictionary<string, object?>
            {
                ["sub"] = sub
            };

            // Include claims permitted by scopes or explicitly requested via the claims parameter.
            if (wantsName)
            {
                var name = principal.FindFirstValue(OidcConstants.Claims.Name) ?? userData?.Name ?? userData?.Username;
                if (!string.IsNullOrEmpty(name)) payload[OidcConstants.Claims.Name] = name;
            }
            if (wantsEmailClaims)
            {
                var email = principal.FindFirstValue(OidcConstants.Claims.Email) ?? userData?.Email;
                if (!string.IsNullOrEmpty(email)) payload[OidcConstants.Claims.Email] = email;

                var emailVerifiedClaim = principal.FindFirst(OidcConstants.Claims.EmailVerified)?.Value;
                if (!string.IsNullOrEmpty(emailVerifiedClaim) && bool.TryParse(emailVerifiedClaim, out var b))
                {
                    payload["email_verified"] = b;
                }
                else if (userData?.EmailVerified is bool verified)
                {
                    payload["email_verified"] = verified;
                }

                // Optional: include array of all emails (primary + verified alternates)
                if (Guid.TryParse(sub, out var userId))
                {
                    var verifiedOnly = true; // configurable later
                    var alt = db.UserAlternativeEmails.AsNoTracking()
                        .Where(a => a.UserId == userId && (!verifiedOnly || a.IsVerified))
                        .Select(a => a.Email)
                        .ToArray();
                    if (!string.IsNullOrEmpty(email) || alt.Length > 0)
                    {
                        payload["emails"] = string.IsNullOrEmpty(email) ? alt : new[] { email }.Concat(alt).ToArray();
                    }
                }
            }

            // Tenants list exposure when tenants scope is granted
            if (scopes.Contains(OidcConstants.Scopes.Tenants))
            {
                var tenantsJson = principal.FindFirstValue(OidcConstants.Scopes.Tenants);
                if (!string.IsNullOrWhiteSpace(tenantsJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(tenantsJson);
                        payload[OidcConstants.Scopes.Tenants] = doc.RootElement.Clone();
                    }
                    catch
                    {
                        // If it's not valid JSON, fall back to string.
                        payload[OidcConstants.Scopes.Tenants] = tenantsJson;
                    }
                }
            }

            // Roles exposure when roles scope is granted
            if (scopes.Contains("roles"))
            {
                // Roles are contextual to the client; infer from azp or aud (prefer azp when present)
                var clientId = principal.FindFirst("azp")?.Value ?? principal.FindFirst("aud")?.Value;
                if (!string.IsNullOrEmpty(clientId))
                {
                    var userSub = principal.FindFirstValue("sub");
                    if (Guid.TryParse(userSub, out var userId))
                    {
                        // Find client record to resolve ClientId (Guid)
                        var client = db.Clients.AsNoTracking().FirstOrDefault(c => c.ClientId == clientId);
                        if (client is not null)
                        {
                            var roleIds = db.UserClientRoleAssignments.AsNoTracking()
                                .Where(a => a.UserId == userId && a.ClientId == client.Id && a.IsActive)
                                .Select(a => a.RoleId);
                            var roles = db.Roles.AsNoTracking()
                                .Where(r => roleIds.Contains(r.Id))
                                .Select(r => r.Name)
                                .ToArray();
                            if (roles.Length > 0)
                            {
                                payload["roles"] = roles;
                            }
                            // Include realm claim
                            payload["realm"] = db.Realms.AsNoTracking().Where(r => r.Id == client.RealmId).Select(r => r.Name).FirstOrDefault();
                        }
                    }
                }
            }

            if (requestedUserInfoClaims is { Count: > 0 })
            {
                // Always include sub; only include other requested claims.
                var keys = payload.Keys.ToArray();
                foreach (var k in keys)
                {
                    if (string.Equals(k, "sub", StringComparison.Ordinal)) continue;
                    if (!requestedUserInfoClaims.Contains(k)) payload.Remove(k);
                }
            }

            if (requestedUserInfoConstraints is { Count: > 0 })
            {
                static string? ScalarToString(object? value)
                {
                    return value switch
                    {
                        null => null,
                        string s => s,
                        bool b => b ? "true" : "false",
                        int i => i.ToString(CultureInfo.InvariantCulture),
                        long l => l.ToString(CultureInfo.InvariantCulture),
                        double d => d.ToString(CultureInfo.InvariantCulture),
                        float f => f.ToString(CultureInfo.InvariantCulture),
                        decimal m => m.ToString(CultureInfo.InvariantCulture),
                        JsonElement el => el.ValueKind switch
                        {
                            JsonValueKind.String => el.GetString(),
                            JsonValueKind.Number => el.TryGetInt64(out var li) ? li.ToString(CultureInfo.InvariantCulture) : el.GetRawText(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            _ => null
                        },
                        _ => value.ToString()
                    };
                }

                static IEnumerable<string> GetAllValues(object? value)
                {
                    if (value is null) return Array.Empty<string>();
                    if (value is string s) return new[] { s };
                    if (value is string[] arr) return arr;
                    if (value is IEnumerable<string> seq) return seq;

                    var scalar = ScalarToString(value);
                    return scalar is null ? Array.Empty<string>() : new[] { scalar };
                }

                foreach (var kvp in requestedUserInfoConstraints)
                {
                    var claimName = kvp.Key;
                    if (string.Equals(claimName, "sub", StringComparison.Ordinal)) continue;

                    var constraint = kvp.Value;
                    payload.TryGetValue(claimName, out var current);
                    var actualValues = GetAllValues(current).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
                    var hasAny = actualValues.Length > 0;

                    // If there is no value constraint, only enforce essential presence.
                    if (constraint.Value is null && (constraint.Values is null || constraint.Values.Length == 0))
                    {
                        if (constraint.Essential && !hasAny)
                        {
                            outcome = "failure";
                            http.Response.Headers["Cache-Control"] = "no-store";
                            return ErrorResults.InvalidRequest($"Essential userinfo claim '{claimName}' is not available.");
                        }
                        continue;
                    }

                    bool matches;
                    if (constraint.Value is not null)
                    {
                        matches = hasAny && actualValues.Any(v => string.Equals(v, constraint.Value, StringComparison.Ordinal));
                    }
                    else
                    {
                        matches = hasAny && actualValues.Any(v => constraint.Values!.Contains(v, StringComparer.Ordinal));
                    }

                    if (matches)
                    {
                        continue;
                    }

                    if (constraint.Essential)
                    {
                        outcome = "failure";
                        http.Response.Headers["Cache-Control"] = "no-store";
                        return ErrorResults.InvalidRequest($"Essential userinfo claim '{claimName}' cannot satisfy the requested value constraint.");
                    }

                    // Not essential: omit the claim.
                    payload.Remove(claimName);
                }
            }

            // Optional: return signed/encrypted JWT UserInfo response when requested by client metadata.
            // This is driven by per-client OIDC metadata fields stored on Client.
            var clientIdForResponse = azp;
            Client? clientForResponse = null;
            if (!string.IsNullOrWhiteSpace(clientIdForResponse))
            {
                clientForResponse = await db.Clients.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.ClientId == clientIdForResponse)
                    .ConfigureAwait(false);
            }

            var wantsSignedUserInfo = !string.IsNullOrWhiteSpace(clientForResponse?.UserInfoSignedResponseAlg);
            var wantsEncryptedUserInfo = !string.IsNullOrWhiteSpace(clientForResponse?.UserInfoEncryptedResponseAlg)
                && !string.IsNullOrWhiteSpace(clientForResponse?.UserInfoEncryptedResponseEnc);

            if (wantsSignedUserInfo || wantsEncryptedUserInfo)
            {
                if (string.IsNullOrWhiteSpace(clientIdForResponse))
                {
                    outcome = "failure";
                    http.Response.Headers["Cache-Control"] = "no-store";
                    return ErrorResults.InvalidRequest("Cannot issue a JWT UserInfo response without a client identifier (azp).");
                }

                // Ensure we are truthful: this OP uses a single active signing algorithm per tenant.
                // If a client requests a different UserInfo signing alg, we fail fast.
                var requestedAlg = clientForResponse?.UserInfoSignedResponseAlg;
                if (!string.IsNullOrWhiteSpace(requestedAlg))
                {
                    if (string.Equals(requestedAlg, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        outcome = "failure";
                        http.Response.Headers["Cache-Control"] = "no-store";
                        return ErrorResults.InvalidRequest("Unsigned (alg=none) UserInfo JWT responses are not supported.");
                    }

                    var tenantAccessor = http.RequestServices.GetService(typeof(ITenantAccessor)) as ITenantAccessor;
                    var tenantId = tenantAccessor?.CurrentTenant?.TenantId;

                    var activeSigningAlg = await db.SigningKeys
                        .AsNoTracking()
                        .Where(k => k.TenantId == tenantId)
                        .OrderByDescending(k => k.CreatedAt)
                        .Select(k => k.Alg)
                        .FirstOrDefaultAsync()
                        .ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(activeSigningAlg))
                    {
                        activeSigningAlg = SecurityConstants.JwtAlgorithms.RS256;
                    }

                    if (!string.Equals(requestedAlg, activeSigningAlg, StringComparison.Ordinal))
                    {
                        outcome = "failure";
                        http.Response.Headers["Cache-Control"] = "no-store";
                        return ErrorResults.InvalidRequest($"Client requests userinfo_signed_response_alg '{requestedAlg}', but this tenant currently signs with '{activeSigningAlg}'.");
                    }
                }

                var claims = BuildClaims(payload);

                var exp = DateTimeOffset.UtcNow.AddMinutes(5);

                string jwtToken;
                if (wantsEncryptedUserInfo)
                {
                    var enc = await TryGetUserInfoEncryptingCredentialsAsync(clientForResponse, http.RequestAborted).ConfigureAwait(false);
                    if (enc is null)
                    {
                        outcome = "failure";
                        http.Response.Headers["Cache-Control"] = "no-store";
                        return ErrorResults.InvalidRequest("Client requests encrypted UserInfo response, but encryption configuration is invalid.");
                    }

                    // Produce a signed+encrypted JWT (nested JWS inside JWE) for maximal interoperability.
                    jwtToken = await jwt.CreateJwtEncryptedAsync(issuer, clientIdForResponse, claims, exp, enc, tokenType: "JWT", ct: http.RequestAborted).ConfigureAwait(false);
                }
                else
                {
                    jwtToken = await jwt.CreateJwtAsync(issuer, clientIdForResponse, claims, exp, tokenType: "JWT", ct: http.RequestAborted).ConfigureAwait(false);
                }

                logger.LogInformation("/userinfo 200 (jwt) for sub_hash={SubHash}", LogTokenization.HashId(payload["sub"]?.ToString()));
                metrics.UserInfoSuccess.Add(1);
                var resultJwt = Results.Text(jwtToken, "application/jwt", Encoding.UTF8);
                return new CacheHeaderResult(resultJwt, "private, max-age=60");
            }

            logger.LogInformation("/userinfo 200 for sub_hash={SubHash}", LogTokenization.HashId(payload["sub"]?.ToString()));
            metrics.UserInfoSuccess.Add(1);
            var resultJson = Results.Json(payload);
            return new CacheHeaderResult(resultJson, "private, max-age=60");
        }
        finally
        {
            sw.Stop();
            metrics.UserInfoDurationMs.Record(sw.Elapsed.TotalMilliseconds, new TagList { new("outcome", outcome) });
        }
    }

    static IResult WithWwwAuthenticate(IResult result, string error = "invalid_token")
        => new WwwAuthenticateResult(result, $"Bearer error=\"{error}\"");

    private async Task<EncryptingCredentials?> TryGetUserInfoEncryptingCredentialsAsync(Client? client, CancellationToken ct)
    {
        if (client is null) return null;
        if (string.IsNullOrWhiteSpace(client.UserInfoEncryptedResponseAlg) || string.IsNullOrWhiteSpace(client.UserInfoEncryptedResponseEnc)) return null;

        // Minimal initial support: RSA-OAEP + A256CBC-HS512 (supported by JwtSecurityTokenHandler).
        if (!string.Equals(client.UserInfoEncryptedResponseAlg, SecurityAlgorithms.RsaOAEP, StringComparison.Ordinal)
            || !string.Equals(client.UserInfoEncryptedResponseEnc, SecurityAlgorithms.Aes256CbcHmacSha512, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var key = await ClientJwksResolver.GetEncryptionKeyAsync(
                client,
                httpClientFactory,
                jwksCache,
                authOptions.Value.ClientJwksCacheSeconds,
                ct).ConfigureAwait(false);

            if (key is null || !string.Equals(key.Kty, "RSA", StringComparison.OrdinalIgnoreCase)) return null;

            return new EncryptingCredentials(key, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512);
        }
        catch
        {
            return null;
        }
    }

    private static List<Claim> BuildClaims(Dictionary<string, object?> payload)
    {
        var claims = new List<Claim>();
        foreach (var kvp in payload)
        {
            var type = kvp.Key;
            var value = kvp.Value;
            switch (value)
            {
                case null:
                    continue;
                case string s:
                    if (!string.IsNullOrWhiteSpace(s)) claims.Add(new Claim(type, s));
                    break;
                case bool b:
                    claims.Add(new Claim(type, b ? "true" : "false", ClaimValueTypes.Boolean));
                    break;
                case string[] arr:
                    foreach (var item in arr)
                    {
                        if (!string.IsNullOrWhiteSpace(item)) claims.Add(new Claim(type, item));
                    }
                    break;
                case JsonElement el:
                    claims.Add(new Claim(type, el.GetRawText(), JsonClaimValueTypes.Json));
                    break;
                default:
                    claims.Add(new Claim(type, value.ToString() ?? string.Empty));
                    break;
            }
        }
        return claims;
    }
}

internal sealed class CacheHeaderResult(IResult inner, string cacheControl) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.Headers["Cache-Control"] = cacheControl;
        return inner.ExecuteAsync(httpContext);
    }
}

internal sealed class WwwAuthenticateResult(IResult inner, string value) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.Headers["WWW-Authenticate"] = value;
        return inner.ExecuteAsync(httpContext);
    }
}
