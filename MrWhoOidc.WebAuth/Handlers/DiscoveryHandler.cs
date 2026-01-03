using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using Microsoft.Extensions.Options;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IDiscoveryHandler
{
    Task<IResult> HandleAsync(HttpContext ctx);
}

public sealed class DiscoveryHandler(
    IOptions<OidcOptions> oidcOptions,
    IOptions<AuthOptions> authOptions,
    AuthDbContext db,
    IFeatureService featureService,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : IDiscoveryHandler
{
    public async Task<IResult> HandleAsync(HttpContext ctx)
    {
        // In multi-tenant deployments, each tenant has its own issuer under /t/{slug}.
        // Enforce that the discovery document is only served from the tenant-prefixed path.
        // (Root-level discovery would be ambiguous and can lead clients to bind to the wrong issuer.)
        if (multiTenancyOptions.Enabled && !ctx.Request.Path.StartsWithSegments("/t", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Tenant required",
                detail: "In multi-tenant mode, use /t/{slug}/.well-known/openid-configuration for discovery.");
        }

        // Build issuer dynamically using PublicBaseUrl configuration or request URL
        // This ensures the issuer reflects the actual public-facing URL (e.g., when running behind proxy/Docker)
        var issuer = ctx.GetIssuer(oidcOptions.Value);
        var baseUrl = issuer.TrimEnd('/');

        // Pull scopes from DB (exposed only). In multi-tenant mode, expose global scopes plus
        // tenant-scoped scopes for the resolved tenant.
        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        var scopesQuery = db.Scopes.AsNoTracking().Where(s => s.IsExposed);
        if (tenantId is not null)
        {
            scopesQuery = scopesQuery.Where(s => s.TenantId == null || s.TenantId == tenantId);
        }
        else
        {
            scopesQuery = scopesQuery.Where(s => s.TenantId == null);
        }

        var scopes = await scopesQuery.Select(s => s.Name).ToArrayAsync(ctx.RequestAborted);
        if (scopes.Length == 0)
        {
            scopes = OidcConstants.Scopes.AllStandardScopes;
        }

        var grants = new List<string>
        {
            OAuthConstants.GrantTypes.AuthorizationCode,
            OAuthConstants.GrantTypes.RefreshToken,
            OAuthConstants.GrantTypes.ClientCredentials
        };
        if (authOptions.Value.EnableTokenExchange)
        {
            grants.Add(OAuthConstants.GrantTypes.TokenExchange);
        }

        // Check if AdvancedSecurity feature is enabled for PAR
        var advancedSecurityEnabled = await featureService.IsFeatureEnabledAsync(
            FeatureFlags.AdvancedSecurity, tenantId, ctx.RequestAborted);

        var requestObjectAlgorithms = (authOptions.Value.RequestObjectAllowedAlgorithms is { Length: > 0 }
            ? authOptions.Value.RequestObjectAllowedAlgorithms
            : new[]
            {
                SecurityConstants.JwtAlgorithms.RS256,
                SecurityConstants.JwtAlgorithms.PS256,
                SecurityConstants.JwtAlgorithms.ES256,
                SecurityConstants.JwtAlgorithms.ES384,
                SecurityConstants.JwtAlgorithms.ES512
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a)
            .ToArray();

        // Advertise supported claims based on the scopes we expose for this tenant.
        // This keeps discovery consistent with what /userinfo can emit.
        var supportedScopes = scopes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var claimsSupportedList = new List<string>
        {
            OidcConstants.Claims.Subject
        };

        if (supportedScopes.Contains(OidcConstants.Scopes.Profile))
        {
            claimsSupportedList.Add(OidcConstants.Claims.Name);
        }

        if (supportedScopes.Contains(OidcConstants.Scopes.Email))
        {
            claimsSupportedList.Add(OidcConstants.Claims.Email);
            claimsSupportedList.Add(OidcConstants.Claims.EmailVerified);
            claimsSupportedList.Add("emails");
        }

        if (supportedScopes.Contains(OidcConstants.Scopes.Tenants))
        {
            // /userinfo exposes this under the same claim name as the scope.
            claimsSupportedList.Add(OidcConstants.Scopes.Tenants);
        }

        if (supportedScopes.Contains(OidcConstants.Scopes.Roles))
        {
            claimsSupportedList.Add(OidcConstants.Claims.Roles);
            claimsSupportedList.Add(OidcConstants.Claims.Realm);
        }

        var claimsSupported = claimsSupportedList
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();

        var body = new Dictionary<string, object>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = $"{baseUrl}/authorize",
            ["token_endpoint"] = $"{baseUrl}/token",
            ["userinfo_endpoint"] = $"{baseUrl}/userinfo",
            ["revocation_endpoint"] = $"{baseUrl}/revoke",
            ["introspection_endpoint"] = $"{baseUrl}/introspect",
            ["introspection_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post", "private_key_jwt" },
            ["subject_types_supported"] = new[] { OidcConstants.SubjectTypes.Public, OidcConstants.SubjectTypes.Pairwise },
            ["introspection_endpoint_auth_signing_alg_values_supported"] = new[]
            {
                SecurityConstants.JwtAlgorithms.RS256,
                SecurityConstants.JwtAlgorithms.RS384,
                SecurityConstants.JwtAlgorithms.RS512,
                SecurityConstants.JwtAlgorithms.ES256,
                SecurityConstants.JwtAlgorithms.ES384,
                SecurityConstants.JwtAlgorithms.ES512
            },
            ["jwks_uri"] = $"{baseUrl}/jwks",
            ["end_session_endpoint"] = $"{baseUrl}/connect/endsession",
            ["frontchannel_logout_supported"] = true,
            ["frontchannel_logout_session_supported"] = true,
            ["backchannel_logout_supported"] = true,
            ["backchannel_logout_session_supported"] = true,
            ["response_types_supported"] = new[] { OAuthConstants.ResponseTypes.Code },
            ["grant_types_supported"] = grants.ToArray(),
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post", "private_key_jwt" },
            ["token_endpoint_auth_signing_alg_values_supported"] = new[]
            {
                SecurityConstants.JwtAlgorithms.RS256,
                SecurityConstants.JwtAlgorithms.RS384,
                SecurityConstants.JwtAlgorithms.RS512,
                SecurityConstants.JwtAlgorithms.ES256,
                SecurityConstants.JwtAlgorithms.ES384,
                SecurityConstants.JwtAlgorithms.ES512
            },
            ["code_challenge_methods_supported"] = new[] { OAuthConstants.CodeChallengeMethods.S256 },
            ["id_token_signing_alg_values_supported"] = new[] { SecurityConstants.JwtAlgorithms.RS256 },
            ["scopes_supported"] = scopes,
            ["claims_supported"] = claimsSupported,
            ["resource_indicators_supported"] = true,
            // JAR support
            ["request_parameter_supported"] = true,
            ["request_uri_parameter_supported"] = true,
            ["request_object_signing_alg_values_supported"] = requestObjectAlgorithms,
            // JARM support
            ["response_modes_supported"] = new[] { "query", "fragment", "form_post", "query.jwt", "form_post.jwt" },
            ["authorization_response_iss_parameter_supported"] = true,
            ["authorization_response_signing_alg_values_supported"] = new[] { SecurityConstants.JwtAlgorithms.RS256 },
            ["authorization_response_encryption_alg_values_supported"] = new[] { "RSA-OAEP" },
            ["authorization_response_encryption_enc_values_supported"] = new[] { "A256GCM" },
            // Non-standard hints to improve DX
            ["introspection_token_types_supported"] = new[] { OAuthConstants.TokenTypes.AccessToken, OAuthConstants.TokenTypes.RefreshToken },
            // DPoP capability hints (experimental)
            ["dpop_signing_alg_values_supported"] = new[] { SecurityConstants.JwtAlgorithms.RS256, SecurityConstants.JwtAlgorithms.ES256 },
            ["dpop_bound_access_tokens"] = true
        };

        // Only advertise PAR endpoint if AdvancedSecurity feature is enabled
        if (advancedSecurityEnabled)
        {
            body["pushed_authorization_request_endpoint"] = $"{baseUrl}/par";
            body["require_pushed_authorization_requests"] = authOptions.Value.RequirePar;
        }

        ctx.Response.Headers["Cache-Control"] = "public, max-age=300";
        return Results.Json(body);
    }
}
