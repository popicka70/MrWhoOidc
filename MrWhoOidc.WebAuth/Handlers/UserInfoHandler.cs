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

        var sub = principal.FindFirstValue("sub");
        var name = principal.FindFirstValue("name");
        var email = principal.FindFirstValue("email");
        var emailVerified = principal.FindFirst("email_verified")?.Value;

        var payload = new Dictionary<string, object?> { ["sub"] = sub };
        if (!string.IsNullOrEmpty(name)) payload["name"] = name;
        if (!string.IsNullOrEmpty(email)) payload["email"] = email;
        if (!string.IsNullOrEmpty(emailVerified)) payload["email_verified"] = bool.TryParse(emailVerified, out var b) ? b : null;

        var result = Results.Json(payload);
        // Set short private cache control header
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
