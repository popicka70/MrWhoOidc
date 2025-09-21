using MrWhoOidc.Auth.Services;
using System.Security.Claims;
using MrWhoOidc.WebAuth.Observability;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MrWhoOidc.WebAuth.Infrastructure;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IUserInfoHandler
{
    IResult Handle(HttpContext http);
}

public sealed class UserInfoHandler(OidcOptions options, ITokenValidator validator, OidcMetrics metrics, IDPoPValidator dpop, IDPoPReplayCache replayCache, IDPoPNonceStore nonceStore, ILogger<UserInfoHandler> logger, AuthDbContext db) : IUserInfoHandler
{
    public IResult Handle(HttpContext http)
    {
        var sw = Stopwatch.StartNew();
        string outcome = "success";
        try
        {
            metrics.UserInfoRequests.Add(1);
            var auth = http.Request.Headers.Authorization.ToString();
            if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                outcome = "failure";
                logger.LogWarning("/userinfo 401: missing or invalid Authorization header from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(Results.Json(new { error = "invalid_token" }, statusCode: 401));
            }

            var token = auth.Substring("Bearer ".Length).Trim();
            var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";

            var (ok, principal, _) = validator.Validate(token, issuer);
            if (!ok || principal is not { })
            {
                outcome = "failure";
                logger.LogWarning("/userinfo 401: token validation failed from {IP}", http.Connection.RemoteIpAddress?.ToString());
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(Results.Json(new { error = "invalid_token" }, statusCode: 401));
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
                    return WithWwwAuthenticate(Results.Json(new { error = "invalid_token" }, statusCode: 401));
                }

                var endpointUrl = (options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}")!.TrimEnd('/') + "/userinfo";
                var validation = dpop.ValidateForEndpointAsync(http, endpointUrl, token).GetAwaiter().GetResult();

                // Nonce challenge support
                var clientIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                (bool nonceOk, string serverNonce) = nonceStore.ValidateOrIssueAsync(endpointUrl, clientIp, validation.Jkt, validation.Nonce).GetAwaiter().GetResult();
                if (!nonceOk)
                {
                    http.Response.Headers["DPoP-Nonce"] = serverNonce;
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=use_dpop_nonce";
                    logger.LogInformation("/userinfo nonce challenge issued to {IP}", clientIp);
                    return Results.Unauthorized();
                }

                if (!validation.Ok)
                {
                    outcome = "failure";
                    logger.LogWarning("/userinfo 401: invalid DPoP proof reason={Reason} from {IP}", validation.Error ?? "unknown", clientIp);
                    metrics.UserInfoFailures.Add(1);
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                    return Results.Json(new { error = "invalid_token" }, statusCode: 401);
                }

                if (string.IsNullOrEmpty(validation.Jkt) || !string.Equals(validation.Jkt, cnfJkt, StringComparison.Ordinal))
                {
                    outcome = "failure";
                    logger.LogWarning("/userinfo 401: cnf.jkt mismatch from {IP}", clientIp);
                    metrics.UserInfoFailures.Add(1);
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                    return Results.Json(new { error = "invalid_token" }, statusCode: 401);
                }

                // Replay protection: DPoP jti must not repeat within window
                if (string.IsNullOrEmpty(validation.Jti) || validation.Iat is null)
                {
                    outcome = "failure";
                    logger.LogWarning("/userinfo 401: DPoP missing jti/iat from {IP}", clientIp);
                    metrics.UserInfoFailures.Add(1);
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                    return Results.Json(new { error = "invalid_token" }, statusCode: 401);
                }
                var key = $"{validation.Jkt}:{validation.Jti}";
                var expires = DateTimeOffset.FromUnixTimeSeconds(validation.Iat.Value).AddMinutes(5);
                if (!replayCache.TryAdd(key, expires))
                {
                    outcome = "failure";
                    logger.LogWarning("/userinfo 401: DPoP replay detected for {Key} from {IP}", key, clientIp);
                    metrics.UserInfoFailures.Add(1);
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=replay";
                    return Results.Json(new { error = "invalid_token" }, statusCode: 401);
                }
            }

            var scopes = (principal.FindFirst("scope")?.Value ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var payload = new Dictionary<string, object?>
            {
                ["sub"] = principal.FindFirstValue("sub")
            };

            // Only include claims permitted by scopes
            if (scopes.Contains("profile"))
            {
                var name = principal.FindFirstValue("name");
                if (!string.IsNullOrEmpty(name)) payload["name"] = name;
            }
            if (scopes.Contains("email"))
            {
                var email = principal.FindFirstValue("email");
                if (!string.IsNullOrEmpty(email)) payload["email"] = email;
                var emailVerified = principal.FindFirst("email_verified")?.Value;
                if (!string.IsNullOrEmpty(emailVerified) && bool.TryParse(emailVerified, out var b))
                    payload["email_verified"] = b;

                // Optional: include array of all emails (primary + verified alternates)
                var sub = principal.FindFirstValue("sub");
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
                        // Find client record to resolve RealmId and ClientId (Guid)
                        var client = db.Clients.AsNoTracking().FirstOrDefault(c => c.ClientId == clientId);
                        if (client is not null)
                        {
                            var roleIds = db.UserRoleAssignments.AsNoTracking()
                                .Where(a => a.UserId == userId && a.ClientId == client.Id && a.RealmId == client.RealmId && a.IsActive)
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

            logger.LogInformation("/userinfo 200 for {Sub}", payload["sub"]);
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
