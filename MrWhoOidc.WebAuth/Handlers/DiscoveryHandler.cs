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
using MrWhoOidc.Auth.Settings;
using System.Text.Json;

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
    IPlatformSettingsService platformSettingsService,
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

        var platformSettings = await platformSettingsService.GetSettingsAsync().ConfigureAwait(false);
        var tokenExchangeEnabled = platformSettings.EnableTokenExchange ?? authOptions.Value.EnableTokenExchange;

        var grants = new List<string>
        {
            OAuthConstants.GrantTypes.AuthorizationCode,
            OAuthConstants.GrantTypes.RefreshToken,
            OAuthConstants.GrantTypes.ClientCredentials
        };

        // JARM encryption is an explicit per-client opt-in. Keep discovery truthful to tenant configuration:
        // only advertise authorization response encryption algorithms when at least one client in this
        // tenant is configured for it.
        if (tokenExchangeEnabled)
        {
            grants.Add(OAuthConstants.GrantTypes.TokenExchange);
        }

        // RFC 8628: Device Authorization Grant
        var deviceAuthEnabled = authOptions.Value.EnableDeviceAuthorizationGrant &&
            await featureService.IsFeatureEnabledAsync(FeatureFlags.DeviceAuthorizationGrant, tenantId, ctx.RequestAborted);
        if (deviceAuthEnabled)
        {
            grants.Add(OAuthConstants.GrantTypes.DeviceCode);
        }

        var clientsQuery = db.Clients.AsNoTracking();
        if (tenantId is not null)
        {
            clientsQuery = clientsQuery.Where(c => c.TenantId == tenantId);
        }

        var advertiseAuthorizationResponseEncryption = await clientsQuery.AnyAsync(
            c => c.AuthorizationEncryptedResponseAlg != null && c.AuthorizationEncryptedResponseEnc != null,
            ctx.RequestAborted);

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

        // Advertise the active tenant signing algorithm for ID tokens (and JARM signing).
        // This keeps discovery consistent with what the server actually emits.
        var activeSigningAlg = await db.SigningKeys
            .AsNoTracking()
            .Where(k => k.TenantId == tenantId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => k.Alg)
            .FirstOrDefaultAsync(ctx.RequestAborted)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(activeSigningAlg))
        {
            activeSigningAlg = SecurityConstants.JwtAlgorithms.RS256;
        }

        var body = new Dictionary<string, object>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = $"{baseUrl}/authorize",
            ["token_endpoint"] = $"{baseUrl}/token",
            ["userinfo_endpoint"] = $"{baseUrl}/userinfo",
            ["revocation_endpoint"] = $"{baseUrl}/revoke",
            ["revocation_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post", "private_key_jwt", "self_signed_tls_client_auth" },
            ["introspection_endpoint"] = $"{baseUrl}/introspect",
            ["introspection_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post", "private_key_jwt", "self_signed_tls_client_auth" },
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
            ["revocation_endpoint_auth_signing_alg_values_supported"] = new[]
            {
                SecurityConstants.JwtAlgorithms.RS256,
                SecurityConstants.JwtAlgorithms.RS384,
                SecurityConstants.JwtAlgorithms.RS512,
                SecurityConstants.JwtAlgorithms.ES256,
                SecurityConstants.JwtAlgorithms.ES384,
                SecurityConstants.JwtAlgorithms.ES512
            },
            ["jwks_uri"] = $"{baseUrl}/jwks",
            // OIDC Session Management (check_session_iframe)
            ["check_session_iframe"] = $"{baseUrl}/connect/checksession",
            ["end_session_endpoint"] = $"{baseUrl}/connect/endsession",
            ["frontchannel_logout_supported"] = true,
            ["frontchannel_logout_session_supported"] = true,
            ["backchannel_logout_supported"] = true,
            ["backchannel_logout_session_supported"] = true,
            ["response_types_supported"] = new[] { OAuthConstants.ResponseTypes.Code },
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post", "private_key_jwt", "self_signed_tls_client_auth" },
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
            ["scopes_supported"] = scopes,
            ["claims_supported"] = claimsSupported,
            // OIDC Discovery recommended metadata
            ["claim_types_supported"] = new[] { "normal" },
            ["claims_parameter_supported"] = true,
            ["display_values_supported"] = new[] { "popup" },
            ["prompt_values_supported"] = new[] { "none", "login", "consent", "select_account" },
            // ui_locales_supported is a best-effort hint (actual locale availability depends on deployed resources)
            ["ui_locales_supported"] = authOptions.Value.UiLocalesSupported,
            // acr_values_supported is optional; only advertise when configured
            ["id_token_signing_alg_values_supported"] = new[] { activeSigningAlg },
            ["id_token_encryption_alg_values_supported"] = new[] { "RSA-OAEP" },
            ["id_token_encryption_enc_values_supported"] = new[] { "A256CBC-HS512" },
            // UserInfo signed/encrypted JWT response support
            ["userinfo_signing_alg_values_supported"] = new[] { activeSigningAlg },
            ["userinfo_encryption_alg_values_supported"] = new[] { "RSA-OAEP" },
            ["userinfo_encryption_enc_values_supported"] = new[] { "A256CBC-HS512" },
            ["acr_values_supported"] = authOptions.Value.AcrValuesSupported,
            ["service_documentation"] = authOptions.Value.ServiceDocumentationUrl ?? string.Empty,
            ["op_policy_uri"] = authOptions.Value.OpPolicyUrl ?? string.Empty,
            ["op_tos_uri"] = authOptions.Value.OpTosUrl ?? string.Empty,
            ["resource_indicators_supported"] = true,
            // JAR support
            ["request_parameter_supported"] = true,
            ["request_uri_parameter_supported"] = true,
            ["request_object_signing_alg_values_supported"] = requestObjectAlgorithms,
            // Request object encryption (JAR encryption) is opt-in; keep discovery truthful.
            // JARM support
            ["response_modes_supported"] = new[] { "query", "fragment", "form_post", "query.jwt", "fragment.jwt", "form_post.jwt" },
            ["authorization_response_iss_parameter_supported"] = true,
            ["authorization_response_signing_alg_values_supported"] = new[] { activeSigningAlg },
            // Non-standard hints to improve DX
            ["introspection_token_types_supported"] = new[] { OAuthConstants.TokenTypes.AccessToken, OAuthConstants.TokenTypes.RefreshToken },
            // DPoP capability hints (experimental)
            ["dpop_signing_alg_values_supported"] = new[] { SecurityConstants.JwtAlgorithms.RS256, SecurityConstants.JwtAlgorithms.ES256 },
            // dpop_bound_access_tokens = true means ALL tokens require DPoP (RFC 9449 §5.1).
            // DPoP is optional per-request here, so we advertise supported algorithms above
            // and leave this flag false to avoid misleading strictly-conformant clients.
            ["dpop_bound_access_tokens"] = false,
            // tls_client_certificate_bound_access_tokens = true means ALL tokens are cert-bound (RFC 8705).
            // mTLS is optional here, so we set this to false.
            ["tls_client_certificate_bound_access_tokens"] = false
        };

        if (authOptions.Value.EnableRequestObjectEncryption)
        {
            body["request_object_encryption_alg_values_supported"] = new[] { "RSA-OAEP" };
            body["request_object_encryption_enc_values_supported"] = new[] { "A256CBC-HS512" };
        }

        if (advertiseAuthorizationResponseEncryption)
        {
            body["authorization_response_encryption_alg_values_supported"] = new[] { "RSA-OAEP" };
            body["authorization_response_encryption_enc_values_supported"] = new[] { "A256CBC-HS512" };
        }

        // Remove empty optional strings to keep the discovery document clean.
        if (body.TryGetValue("service_documentation", out var sd) && sd is string s1 && string.IsNullOrWhiteSpace(s1)) body.Remove("service_documentation");
        if (body.TryGetValue("op_policy_uri", out var pp) && pp is string s2 && string.IsNullOrWhiteSpace(s2)) body.Remove("op_policy_uri");
        if (body.TryGetValue("op_tos_uri", out var tos) && tos is string s3 && string.IsNullOrWhiteSpace(s3)) body.Remove("op_tos_uri");

        // If UI locales are not configured, omit the field.
        if (authOptions.Value.UiLocalesSupported is null || authOptions.Value.UiLocalesSupported.Length == 0)
        {
            body.Remove("ui_locales_supported");
        }

        // If ACR values are not configured, omit the field.
        if (authOptions.Value.AcrValuesSupported is null || authOptions.Value.AcrValuesSupported.Length == 0)
        {
            body.Remove("acr_values_supported");
        }

        // RFC 7591/7592: Dynamic Client Registration endpoint
        // Keep discovery truthful: advertise only when DCR is effectively usable for this tenant.
        if (authOptions.Value.EnableDynamicClientRegistration)
        {
            if (platformSettings.DynamicClientRegistrationEnabled)
            {
                var dcrRealmId = await GetDynamicClientRegistrationRealmIdAsync(tenantAccessor.CurrentTenant?.TenantId, ctx.RequestAborted)
                    .ConfigureAwait(false);
                if (dcrRealmId != null)
                {
                    body["registration_endpoint"] = $"{baseUrl}/register";
                }
            }
        }

        // Only advertise PAR endpoint if AdvancedSecurity feature is enabled
        if (advancedSecurityEnabled)
        {
            body["pushed_authorization_request_endpoint"] = $"{baseUrl}/par";
            body["require_pushed_authorization_requests"] = authOptions.Value.RequirePar;
        }

        // RFC 8628: Device Authorization Grant endpoint
        if (deviceAuthEnabled)
        {
            body["device_authorization_endpoint"] = $"{baseUrl}/device/authorize";
        }

        // OpenID Connect CIBA Core 1.0
        if (authOptions.Value.EnableCiba)
        {
            grants.Add(OAuthConstants.GrantTypes.Ciba);
            body["backchannel_authentication_endpoint"] = $"{baseUrl}/bc-authorize";
            body["backchannel_token_delivery_modes_supported"] = authOptions.Value.CibaTokenDeliveryModesSupported;
            body["backchannel_authentication_request_signing_alg_values_supported"] = new[]
            {
                SecurityConstants.JwtAlgorithms.RS256,
                SecurityConstants.JwtAlgorithms.RS384,
                SecurityConstants.JwtAlgorithms.RS512,
                SecurityConstants.JwtAlgorithms.ES256,
                SecurityConstants.JwtAlgorithms.ES384,
                SecurityConstants.JwtAlgorithms.ES512
            };
            body["backchannel_user_code_parameter_supported"] = authOptions.Value.CibaUserCodeParameterSupported;
        }

        body["grant_types_supported"] = grants.ToArray();

        // RFC 9396: Rich Authorization Requests
        // No specific type restrictions — all authorization_details types are accepted.
        body["authorization_details_types_supported"] = Array.Empty<string>();

        // RFC 8705: mtls_endpoint_aliases (optional)
        var mtlsBase = authOptions.Value.MtlsEndpointAliasesBaseUrl;
        if (!string.IsNullOrWhiteSpace(mtlsBase) && Uri.TryCreate(mtlsBase.Trim(), UriKind.Absolute, out var mtlsUri))
        {
            var mtlsBaseUrl = mtlsUri.ToString().TrimEnd('/');
            body["mtls_endpoint_aliases"] = new Dictionary<string, string>
            {
                ["token_endpoint"] = $"{mtlsBaseUrl}/token",
                ["introspection_endpoint"] = $"{mtlsBaseUrl}/introspect",
                ["revocation_endpoint"] = $"{mtlsBaseUrl}/revoke"
            };
        }

        ctx.Response.Headers["Cache-Control"] = "public, max-age=300";
        return Results.Json(body);
    }

    private async Task<Guid?> GetDynamicClientRegistrationRealmIdAsync(Guid? tenantId, CancellationToken ct)
    {
        if (tenantId is null || tenantId.Value == Guid.Empty)
        {
            return null;
        }

        var settingsJson = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId.Value)
            .Select(t => t.SettingsJson)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<TenantSettings>(settingsJson);
            return settings?.Auth?.DynamicClientRegistrationRealmId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
