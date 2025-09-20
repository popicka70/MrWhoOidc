using System.Security.Claims;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IUserInfoHandler
{
    IResult Handle(HttpContext http);
}

public sealed class UserInfoHandler(OidcOptions options, ITokenValidator validator) : IUserInfoHandler
{
    public IResult Handle(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.Ordinal))
            return WithWwwAuthenticate(Results.Json(new { error = "invalid_token" }, statusCode: 401));

        var token = auth.Substring("Bearer ".Length).Trim();
        var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";

        var (ok, principal, _) = validator.Validate(token, issuer);
        if (!ok || principal is null)
            return WithWwwAuthenticate(Results.Json(new { error = "invalid_token" }, statusCode: 401));

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

        var result = Results.Json(payload);
        return new CacheHeaderResult(result, "private, max-age=60");
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
