using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IDiscoveryHandler
{
    IResult Handle(HttpContext ctx);
}

public sealed class DiscoveryHandler(OidcOptions options) : IDiscoveryHandler
{
    public IResult Handle(HttpContext ctx)
    {
        var issuer = options.Issuer ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var baseUrl = issuer.TrimEnd('/');
        var body = new
        {
            issuer,
            authorization_endpoint = $"{baseUrl}/authorize",
            token_endpoint = $"{baseUrl}/token",
            userinfo_endpoint = $"{baseUrl}/userinfo",
            revocation_endpoint = $"{baseUrl}/revoke",
            jwks_uri = $"{baseUrl}/jwks",
            end_session_endpoint = $"{baseUrl}/connect/endsession",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post" },
            code_challenge_methods_supported = new[] { "S256" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            scopes_supported = new[] { "openid", "profile", "email" }
        };
        ctx.Response.Headers["Cache-Control"] = "public, max-age=300";
        return Results.Json(body);
    }
}
