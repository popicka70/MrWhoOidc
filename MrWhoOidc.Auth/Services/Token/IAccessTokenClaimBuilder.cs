using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Domain service for building access token claims.
/// </summary>
public interface IAccessTokenClaimBuilder
{
    /// <summary>
    /// Builds the set of claims for an access token.
    /// </summary>
    Task<IEnumerable<Claim>> BuildClaimsAsync(AccessTokenClaimRequest request, CancellationToken ct = default);
}

/// <summary>
/// Request parameters for building access token claims.
/// </summary>
public record AccessTokenClaimRequest(
    Guid UserId,
    string ClientId,
    string[] Scopes,
    string Issuer,
    string? DpopJkt = null,
    string? EntitlementsClaimJson = null,
    string? TenantsClaimJson = null,
    string? RealmName = null,
    string[]? RoleNames = null,
    string? UpstreamIdp = null,
    string? UpstreamAcr = null,
    IEnumerable<string>? CombinedAmr = null,
    IDictionary<string, string>? MappedClaims = null,
    Guid? TenantId = null,
    string? Subject = null
);
