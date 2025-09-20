using MrWhoOidc.Auth.Services;
using System.Security.Claims;
using MrWhoOidc.WebAuth.Observability;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MrWhoOidc.WebAuth.Infrastructure;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IUserInfoHandler
{
    IResult Handle(HttpContext http);
}

public sealed class UserInfoHandler(OidcOptions options, ITokenValidator validator, OidcMetrics metrics, IDPoPValidator dpop, IDPoPReplayCache replayCache) : IUserInfoHandler
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
                metrics.UserInfoFailures.Add(1);
                return WithWwwAuthenticate(Results.Json(new { error = "invalid_token" }, statusCode: 401));
            }

            var token = auth.Substring("Bearer ".Length).Trim();
            var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";

            var (ok, principal, _) = validator.Validate(token, issuer);
            if (!ok || principal is not null == false)
            {
                outcome = "failure";
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
                    using var cnfDoc = JsonDocument.Parse(cnfRaw);
                    if (cnfDoc.RootElement.TryGetProperty("jkt", out var jktProp))
                    {
                        cnfJkt = jktProp.GetString();
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(cnfJkt))
                {
                    outcome = "failure";
                    metrics.UserInfoFailures.Add(1);
                    return WithWwwAuthenticate(Results.Json(new { error = "invalid_token" }, statusCode: 401));
                }

                var endpointUrl = (options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}")!.TrimEnd('/') + "/userinfo";
                var result = dpop.ValidateForEndpointAsync(http, endpointUrl, token).GetAwaiter().GetResult();
                if (!result.Ok || string.IsNullOrEmpty(result.Jkt) || !string.Equals(result.Jkt, cnfJkt, StringComparison.Ordinal))
                {
                    outcome = "failure";
                    metrics.UserInfoFailures.Add(1);
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                    return Results.Json(new { error = "invalid_token" }, statusCode: 401);
                }

                // Replay protection: DPoP jti must not repeat within window
                if (string.IsNullOrEmpty(result.Jti) || result.Iat is null)
                {
                    outcome = "failure";
                    metrics.UserInfoFailures.Add(1);
                    http.Response.Headers["WWW-Authenticate"] = "DPoP error=invalid_dpop";
                    return Results.Json(new { error = "invalid_token" }, statusCode: 401);
                }
                var key = $"{result.Jkt}:{result.Jti}";
                var expires = DateTimeOffset.FromUnixTimeSeconds(result.Iat.Value).AddMinutes(5);
                if (!replayCache.TryAdd(key, expires))
                {
                    outcome = "failure";
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
            }

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
