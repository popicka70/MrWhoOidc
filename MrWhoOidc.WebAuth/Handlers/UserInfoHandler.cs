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

namespace MrWhoOidc.WebAuth.Handlers;

public interface IUserInfoHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class UserInfoHandler(OidcOptions options, IOptions<AuthOptions> authOptions, ITokenValidator validator, OidcEndpointMetrics metrics, IDPoPValidator dpop, IDPoPReplayCache replayCache, IDPoPNonceStore nonceStore, ILogger<UserInfoHandler> logger, AuthDbContext db) : IUserInfoHandler
{
    private sealed record ClaimConstraint(bool Essential, string? Value, string[]? Values);

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
            if (string.IsNullOrEmpty(auth) || !auth.StartsWith(bearerPrefix, StringComparison.Ordinal))
            {
                outcome = "failure";
                logger.LogWarning("/userinfo 401: missing or invalid Authorization header from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(ErrorResults.InvalidToken());
            }

            var token = auth.Substring(bearerPrefix.Length).Trim();
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
                var validation = dpop.ValidateForEndpointAsync(http, endpointUrl, token).GetAwaiter().GetResult();

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
                (bool nonceOk, string serverNonce) = nonceStore
                    .ValidateOrIssueAsync(endpointUrl, clientIp, validation.Jkt, validation.Nonce)
                    .GetAwaiter()
                    .GetResult();
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

            // Resolve user data from DB when the token does not carry profile/email claims.
            // This keeps access tokens lean while allowing /userinfo to return scoped profile data.
            (string? Name, string? Email, bool? EmailVerified)? userData = null;
            if (!string.IsNullOrWhiteSpace(sub) &&
                (scopes.Contains(OidcConstants.Scopes.Profile) || scopes.Contains(OidcConstants.Scopes.Email)) &&
                Guid.TryParse(sub, out var lookupUserId))
            {
                userData = await db.Users.AsNoTracking()
                    .Where(u => u.Id == lookupUserId)
                    .Select(u => new ValueTuple<string?, string?, bool?>(u.Name, u.Email, u.EmailVerified))
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);
            }

            var payload = new Dictionary<string, object?>
            {
                ["sub"] = sub
            };

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

            // Only include claims permitted by scopes
            if (scopes.Contains(OidcConstants.Scopes.Profile))
            {
                var name = principal.FindFirstValue(OidcConstants.Claims.Name) ?? userData?.Name;
                if (!string.IsNullOrEmpty(name)) payload[OidcConstants.Claims.Name] = name;
            }
            if (scopes.Contains(OidcConstants.Scopes.Email))
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

    static IResult WithWwwAuthenticate(IResult result)
        => new WwwAuthenticateResult(result, "Bearer error=\"invalid_token\"");
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
