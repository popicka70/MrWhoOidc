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
            return Results.Unauthorized();

        var token = auth.Substring("Bearer ".Length).Trim();
        var issuer = options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}";

        var (ok, principal, _) = validator.Validate(token, issuer);
        if (!ok || principal is null) return Results.Unauthorized();

        var sub = principal.FindFirstValue("sub");
        return Results.Json(new { sub });
    }
}
