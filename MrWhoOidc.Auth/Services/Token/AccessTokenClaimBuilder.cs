using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Protocols;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Implementation of IAccessTokenClaimBuilder that extracts claim building logic from TokenService.
/// </summary>
public sealed class AccessTokenClaimBuilder(
    IScopeResolver scopeResolver,
    IRoleClaimBuilder roleClaimBuilder,
    IOptions<AuthOptions> authOptions) : IAccessTokenClaimBuilder
{
    public Task<IEnumerable<Claim>> BuildClaimsAsync(AccessTokenClaimRequest request, CancellationToken ct = default)
    {
        var subject = request.Subject ?? request.UserId.ToString();
        var claims = new List<Claim>
        {
            new(OidcConstants.Claims.Subject, subject),
            new(OAuthConstants.Parameters.Scope, string.Join(' ', request.Scopes)),
            new("jti", Guid.NewGuid().ToString("N"))
        };

        if (!string.IsNullOrWhiteSpace(request.EntitlementsClaimJson))
        {
            claims.Add(new("entitlements", request.EntitlementsClaimJson));
        }

        if (!string.IsNullOrWhiteSpace(request.TenantsClaimJson))
        {
            claims.Add(new(OidcConstants.Scopes.Tenants, request.TenantsClaimJson));
        }

        // Add tenant_id claim if any custom (non-standard) scopes are granted
        var hasCustomScopes = request.Scopes.Any(s => !scopeResolver.IsStandardScope(s));
        if (hasCustomScopes && request.TenantId.HasValue && request.TenantId.Value != Guid.Empty)
        {
            claims.Add(new(OidcConstants.Claims.TenantId, request.TenantId.Value.ToString()));
        }

        if (!string.IsNullOrEmpty(request.DpopJkt))
        {
            var cnf = JsonSerializer.Serialize(new { jkt = request.DpopJkt });
            claims.Add(new(OidcConstants.Claims.Cnf, cnf));
        }

        if (request.Scopes.Contains(OidcConstants.Scopes.Roles) && request.RoleNames?.Length > 0)
        {
            claims.AddRange(roleClaimBuilder.BuildRoleClaims(request.RoleNames));
        }

        if (!string.IsNullOrEmpty(request.RealmName))
        {
            claims.Add(new(OidcConstants.Claims.Realm, request.RealmName));
        }

        // Propagate upstream context
        if (!string.IsNullOrWhiteSpace(request.UpstreamIdp))
        {
            claims.Add(new(OidcConstants.Claims.Idp, request.UpstreamIdp));
        }

        if (!string.IsNullOrWhiteSpace(request.UpstreamAcr))
        {
            claims.Add(new(OidcConstants.Claims.Acr, request.UpstreamAcr));
        }

        if (authOptions.Value.EmitAmrInAccessToken && request.CombinedAmr != null)
        {
            foreach (var amr in request.CombinedAmr)
            {
                claims.Add(new(OidcConstants.Claims.Amr, amr));
            }
        }

        // Propagate mapped claims
        var allowAccess = authOptions.Value.PropagateMappedClaimsToAccessToken ?? Array.Empty<string>();
        if (allowAccess.Length > 0 && request.MappedClaims != null && request.MappedClaims.Count > 0)
        {
            foreach (var name in allowAccess)
            {
                if (string.Equals(name, OidcConstants.Claims.Amr, StringComparison.Ordinal)) continue;
                if (request.MappedClaims.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val))
                {
                    claims.Add(new(name, val));
                }
            }
        }

        return Task.FromResult<IEnumerable<Claim>>(claims);
    }
}
