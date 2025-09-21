using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using MrWhoOidc.WebAuth.Infrastructure;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IDiscoveryHandler
{
    IResult Handle(HttpContext ctx);
}

public sealed class DiscoveryHandler(OidcOptions oidcOptions, IOptions<AuthOptions> authOptions) : IDiscoveryHandler
{
    public IResult Handle(HttpContext ctx)
    {
        var issuer = oidcOptions.Issuer ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var baseUrl = issuer.TrimEnd('/');
        var body = new
        {
            issuer,
            authorization_endpoint = $"{baseUrl}/authorize",
            token_endpoint = $"{baseUrl}/token",
            userinfo_endpoint = $"{baseUrl}/userinfo",
            revocation_endpoint = $"{baseUrl}/revoke",
            introspection_endpoint = $"{baseUrl}/introspect",
            introspection_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post", "private_key_jwt" },
            introspection_endpoint_auth_signing_alg_values_supported = new[] { "RS256", "RS384", "RS512", "ES256", "ES384", "ES512" },
            pushed_authorization_request_endpoint = $"{baseUrl}/par",
            require_pushed_authorization_requests = authOptions.Value.RequirePar,
            jwks_uri = $"{baseUrl}/jwks",
            end_session_endpoint = $"{baseUrl}/connect/endsession",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post", "private_key_jwt" },
            token_endpoint_auth_signing_alg_values_supported = new[] { "RS256", "RS384", "RS512", "ES256", "ES384", "ES512" },
            code_challenge_methods_supported = new[] { "S256" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            scopes_supported = new[] { "openid", "profile", "email" },
            resource_indicators_supported = true,
            // JAR support
            request_parameter_supported = true,
            request_uri_parameter_supported = true,
            request_object_signing_alg_values_supported = new[] { "RS256", "ES256" },
            // JARM support
            response_modes_supported = new[] { "query", "fragment", "form_post", "query.jwt", "form_post.jwt" },
            authorization_response_iss_parameter_supported = true,
            authorization_response_signing_alg_values_supported = new[] { "RS256" },
            // Non-standard hints to improve DX
            introspection_token_types_supported = new[] { "access_token", "refresh_token" },
            // DPoP capability hints (experimental)
            dpop_signing_alg_values_supported = new[] { "RS256", "ES256" },
            dpop_bound_access_tokens = true
        };
        ctx.Response.Headers["Cache-Control"] = "public, max-age=300";
        return Results.Json(body);
    }
}
