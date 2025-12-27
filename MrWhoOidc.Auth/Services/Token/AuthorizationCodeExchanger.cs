using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Implementation of IAuthorizationCodeExchanger that extracts logic from TokenService.
/// </summary>
public sealed class AuthorizationCodeExchanger(
    AuthDbContext db,
    IJwtService jwt,
    IRefreshTokenService refreshTokens,
    IRevocationService revocations,
    IOptions<AuthOptions> authOptions,
    IAuthorizationCodeMetadataStore meta,
    ITenantSettingsService settingsService,
    IScopeResolver scopeResolver,
    IEntitlementsProvider entitlementsProvider,
    ITenantsClaimService tenantsClaimService,
    IAccessTokenClaimBuilder claimBuilder,
    ITokenLifetimeResolver lifetimeResolver,
    IOpaqueTokenPolicy opaquePolicy,
    ILogger<AuthorizationCodeExchanger> logger) : IAuthorizationCodeExchanger
{
    private static readonly JsonSerializerOptions EntitlementsJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(bool ok, object? payload, string? error, int status)> ExchangeAsync(AuthorizationCodeExchangeRequest request, CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<(bool ok, object? payload, string? error, int status)>(async () =>
        {
            using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var entity = await db.AuthorizationCodes.FirstOrDefaultAsync(c => c.Code == request.Code, ct).ConfigureAwait(false);
                if (entity is null || entity.Consumed || entity.ExpiresAt < DateTimeOffset.UtcNow)
                {
                    if (entity is not null && entity.Consumed)
                    {
                        await revocations.RevokeAllForUserAsync(entity.UserId, entity.ClientId, ct).ConfigureAwait(false);
                    }
                    return (false, new { error = OAuthConstants.ErrorCodes.InvalidGrant }, OAuthConstants.ErrorCodes.InvalidGrant, 400);
                }

                if (!string.Equals(entity.RedirectUri, request.RedirectUri, StringComparison.Ordinal))
                    return (false, new { error = OAuthConstants.ErrorCodes.InvalidGrant }, OAuthConstants.ErrorCodes.InvalidGrant, 400);

                if (!string.Equals(entity.ClientId, request.ClientId, StringComparison.Ordinal))
                    return (false, new { error = OAuthConstants.ErrorCodes.InvalidGrant }, OAuthConstants.ErrorCodes.InvalidGrant, 400);

                // Validate PKCE S256
                if (!string.IsNullOrEmpty(entity.CodeChallenge))
                {
                    var s256 = CryptoHelper.ComputePkceS256(request.CodeVerifier);
                    if (!string.Equals(s256, entity.CodeChallenge, StringComparison.Ordinal))
                        return (false, new { error = OAuthConstants.ErrorCodes.InvalidGrant }, OAuthConstants.ErrorCodes.InvalidGrant, 400);
                }

                var scopes = JsonSerializer.Deserialize<string[]>(entity.ScopesJson) ?? Array.Empty<string>();

                // RFC 8707: prefer resource indicator as access token audience when present
                string audience;
                if (meta.TryGetResource(request.Code, out var resource) && !string.IsNullOrWhiteSpace(resource))
                {
                    audience = resource;
                }
                else
                {
                    audience = (authOptions.Value.ApiAudiences?.FirstOrDefault()) ?? "api";
                }

                var opaqueEnabled = opaquePolicy.ShouldUseOpaqueAccessToken(audience);

                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == entity.UserId, ct).ConfigureAwait(false);
                var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == request.ClientId, ct).ConfigureAwait(false);

                Guid? tenantIdForEntitlements = request.TenantId ?? user?.TenantId;
                if (tenantIdForEntitlements == Guid.Empty) tenantIdForEntitlements = null;

                var (scopesFiltered, entitlementsClaimJson, signedLicenseTokens) = await ApplyProductEntitlementsAsync(
                    subjectId: entity.UserId.ToString(),
                    tenantId: tenantIdForEntitlements,
                    requestedScopes: scopes,
                    issuer: request.Issuer,
                    ct: ct).ConfigureAwait(false);
                scopes = scopesFiltered;

                string? tenantsClaimJson = null;
                if (scopes.Contains(OidcConstants.Scopes.Tenants, StringComparer.Ordinal))
                {
                    tenantsClaimJson = await tenantsClaimService.BuildTenantsClaimJsonAsync(entity.UserId, ct).ConfigureAwait(false);
                }

                string? realmName = null;
                string[] roleNames = Array.Empty<string>();
                if (client is not null)
                {
                    realmName = await db.Realms.AsNoTracking().Where(r => r.Id == client.RealmId).Select(r => r.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                    if (scopes.Contains("roles"))
                    {
                        var realmRoleNamesQuery = db.UserRealmRoleAssignments.AsNoTracking()
                            .Where(a => a.UserId == entity.UserId && a.RealmId == client.RealmId && a.IsActive)
                            .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
                            .Where(x => x.r.IsActive)
                            .Select(x => x.r.Name);

                        var clientRoleNamesQuery = db.UserClientRoleAssignments.AsNoTracking()
                            .Where(a => a.UserId == entity.UserId && a.ClientId == client.Id && a.IsActive)
                            .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
                            .Where(x => x.r.IsActive)
                            .Select(x => x.r.Name);

                        roleNames = await realmRoleNamesQuery.Union(clientRoleNamesQuery).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
                    }
                }

                meta.TryGetUpstream(request.Code, out var upstreamIdp, out var upstreamAcr, out var upstreamAmrStr);
                var upstreamAmrs = string.IsNullOrWhiteSpace(upstreamAmrStr)
                    ? Array.Empty<string>()
                    : upstreamAmrStr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                meta.TryGetMappedClaims(request.Code, out var mappedClaimsReadOnly);
                var mappedClaims = mappedClaimsReadOnly is null ? new Dictionary<string, string>() : new Dictionary<string, string>(mappedClaimsReadOnly);

                var combinedAmr = new HashSet<string>(StringComparer.Ordinal);
                if (authOptions.Value.EmitAmrInAccessToken || authOptions.Value.EmitAmrInIdToken)
                {
                    foreach (var a in upstreamAmrs) combinedAmr.Add(a);
                    if (mappedClaims.TryGetValue("amr", out var mappedAmr) && !string.IsNullOrWhiteSpace(mappedAmr))
                    {
                        foreach (var a in mappedAmr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            combinedAmr.Add(a);
                    }
                }

                var settings = await settingsService.GetCurrentTenantSettingsAsync().ConfigureAwait(false);
                var accessTokenLifetime = lifetimeResolver.ResolveAccessTokenLifetime(client!, settings);
                var idTokenLifetime = lifetimeResolver.ResolveIdentityTokenLifetime(client!, settings);

                string accessToken;
                if (opaqueEnabled)
                {
                    var jti = Guid.NewGuid().ToString("N");
                    var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                    await PersistOpaqueAccessAsync(entity.UserId, request.ClientId, audience, scopes, jti, raw, accessTokenLifetime, request.DpopJkt, ct).ConfigureAwait(false);
                    accessToken = raw;
                }
                else
                {
                    var claimRequest = new AccessTokenClaimRequest(
                        UserId: entity.UserId,
                        ClientId: request.ClientId,
                        Scopes: scopes,
                        Issuer: request.Issuer,
                        DpopJkt: request.DpopJkt,
                        TenantsClaimJson: tenantsClaimJson,
                        EntitlementsClaimJson: entitlementsClaimJson,
                        RealmName: realmName,
                        RoleNames: roleNames,
                        UpstreamIdp: upstreamIdp,
                        UpstreamAcr: upstreamAcr,
                        CombinedAmr: combinedAmr,
                        MappedClaims: mappedClaims,
                        TenantId: tenantIdForEntitlements
                    );

                    var accessClaims = await claimBuilder.BuildClaimsAsync(claimRequest, ct).ConfigureAwait(false);
                    
                    // Add signed license tokens if present
                    var claimsList = accessClaims.ToList();
                    if (signedLicenseTokens is { Count: > 0 })
                    {
                        var licenseJson = JsonSerializer.Serialize(signedLicenseTokens, EntitlementsJsonOptions);
                        claimsList.Add(new System.Security.Claims.Claim("license", licenseJson));
                    }

                    accessToken = await jwt.CreateJwtAsync(request.Issuer, audience, claimsList, DateTimeOffset.UtcNow.Add(accessTokenLifetime), tokenType: SecurityConstants.JwtTokenTypes.AtJwt, ct: ct).ConfigureAwait(false);
                }

                var atHash = CryptoHelper.ComputeLeftHalfSha256Base64Url(accessToken);

                var idClaims = new List<System.Security.Claims.Claim>
                {
                    new(OidcConstants.Claims.Subject, entity.UserId.ToString())
                };

                if (user is not null)
                {
                    if (scopes.Contains(OidcConstants.Scopes.Profile) && !string.IsNullOrEmpty(user.Name))
                        idClaims.Add(new(OidcConstants.Claims.Name, user.Name));
                    if (scopes.Contains(OidcConstants.Scopes.Email) && !string.IsNullOrEmpty(user.Email))
                    {
                        idClaims.Add(new(OidcConstants.Claims.Email, user.Email));
                        idClaims.Add(new(OidcConstants.Claims.EmailVerified, user.EmailVerified ? "true" : "false"));
                    }
                    if (scopes.Contains(OidcConstants.Scopes.Roles) && roleNames.Length > 0)
                    {
                        foreach (var r in roleNames) idClaims.Add(new(OidcConstants.Claims.Roles, r));
                    }
                    if (!string.IsNullOrEmpty(realmName))
                    {
                        idClaims.Add(new(OidcConstants.Claims.Realm, realmName));
                    }
                }

                if (!string.IsNullOrWhiteSpace(tenantsClaimJson))
                {
                    idClaims.Add(new(OidcConstants.Scopes.Tenants, tenantsClaimJson));
                }

                DateTimeOffset? authTime = null;
                if (meta.TryGetAuthTime(request.Code, out var at)) authTime = at;

                if (!string.IsNullOrWhiteSpace(upstreamIdp)) idClaims.Add(new(OidcConstants.Claims.Idp, upstreamIdp!));
                if (!string.IsNullOrWhiteSpace(upstreamAcr)) idClaims.Add(new(OidcConstants.Claims.Acr, upstreamAcr!));
                if (authOptions.Value.EmitAmrInIdToken)
                {
                    foreach (var amr in combinedAmr) idClaims.Add(new(OidcConstants.Claims.Amr, amr));
                }

                var allowId = authOptions.Value.PropagateMappedClaimsToIdToken ?? Array.Empty<string>();
                if (allowId.Length > 0 && mappedClaims.Count > 0)
                {
                    foreach (var name in allowId)
                    {
                        if (string.Equals(name, OidcConstants.Claims.Amr, StringComparison.Ordinal)) continue;
                        if (mappedClaims.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val))
                        {
                            idClaims.Add(new(name, val));
                        }
                    }
                }

                if (meta.TryGetSid(request.Code, out var sid) && !string.IsNullOrWhiteSpace(sid))
                {
                    idClaims.Add(new(OidcConstants.Claims.Sid, sid!));
                }

                var idToken = await jwt.CreateJwtAsync(
                    request.Issuer,
                    request.ClientId,
                    idClaims,
                    DateTimeOffset.UtcNow.Add(idTokenLifetime),
                    nonce: entity.Nonce,
                    accessTokenHash: atHash,
                    authTime: authTime,
                    ct: ct
                ).ConfigureAwait(false);

                var (refreshToken, _) = await refreshTokens.CreateRefreshTokenAsync(entity.UserId, request.ClientId, scopes, request.IpAddress, request.UserAgent, ct).ConfigureAwait(false);

                entity.Consumed = true;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                meta.Remove(request.Code);

                var payload = new
                {
                    access_token = accessToken,
                    id_token = idToken,
                    refresh_token = refreshToken,
                    token_type = OAuthConstants.TokenTypes.Bearer,
                    expires_in = (int)accessTokenLifetime.TotalSeconds,
                    scope = string.Join(' ', scopes)
                };
                return (true, payload, null, 200);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during authorization code exchange");
                throw;
            }
        });
    }

    private async Task<(string[] scopes, string? entitlementsClaimJson, Dictionary<string, string>? signedLicenseTokens)> ApplyProductEntitlementsAsync(
        string subjectId,
        Guid? tenantId,
        string[] requestedScopes,
        string issuer,
        CancellationToken ct)
    {
        var productScopes = requestedScopes
            .Where(ProductScopeClassifier.IsProductScope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (productScopes.Length == 0)
        {
            return (requestedScopes, null, null);
        }

        string? tenantIdStr = tenantId.HasValue && tenantId.Value != Guid.Empty ? tenantId.Value.ToString() : null;

        IReadOnlyDictionary<string, Entitlements.Contracts.Entitlement> entitlements;
        try
        {
            entitlements = await entitlementsProvider.GetEffectiveEntitlementsAsync(subjectId, tenantIdStr, productScopes, issuer, ct).ConfigureAwait(false);
        }
        catch
        {
            entitlements = new Dictionary<string, Entitlements.Contracts.Entitlement>(StringComparer.OrdinalIgnoreCase);
        }

        var grantedProducts = productScopes.Where(p => entitlements.ContainsKey(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filteredScopes = requestedScopes
            .Where(s => !ProductScopeClassifier.IsProductScope(s) || grantedProducts.Contains(s))
            .ToArray();

        if (grantedProducts.Count == 0)
        {
            return (filteredScopes, null, null);
        }

        var claimObj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in grantedProducts)
        {
            var e = entitlements[p];
            claimObj[p] = new { tier = e.Tier, source = e.Source, licenseId = e.LicenseId, status = e.Status };
        }

        var json = JsonSerializer.Serialize(claimObj, EntitlementsJsonOptions);
        
        var signedTokens = await RequestSignedLicenseTokensAsync(subjectId, tenantIdStr, grantedProducts, issuer, ct).ConfigureAwait(false);
        
        return (filteredScopes, json, signedTokens);
    }

    private async Task<Dictionary<string, string>?> RequestSignedLicenseTokensAsync(
        string subjectId,
        string? tenantId,
        ISet<string> grantedProducts,
        string issuer,
        CancellationToken ct)
    {
        if (entitlementsProvider is not ILicensingEntitlementsClient client)
        {
            return null;
        }

        var signedTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var productKey in grantedProducts)
        {
            try
            {
                var request = new Entitlements.Contracts.SignedLicenseTokenRequest
                {
                    SubjectId = subjectId,
                    ProductKey = productKey,
                    TenantId = tenantId
                };

                var result = await client.GetSignedLicenseTokenAsync(request, issuer, ct).ConfigureAwait(false);
                if (result.Success && result.Response?.Token is not null)
                {
                    signedTokens[productKey] = result.Response.Token;
                }
            }
            catch
            {
                // Log and continue
            }
        }

        return signedTokens.Count > 0 ? signedTokens : null;
    }

    private async Task PersistOpaqueAccessAsync(Guid userId, string clientId, string audience, string[] scopes, string jti, string rawToken, TimeSpan lifetime, string? cnfJkt, CancellationToken ct)
    {
        var hash = CryptoHelper.ComputeSha256Base64(rawToken);
        var entity = new Persistence.Token
        {
            Type = "access",
            TokenHash = hash,
            UserId = userId,
            ClientId = clientId,
            ScopesJson = JsonSerializer.Serialize(scopes),
            Audience = audience,
            Jti = jti,
            CnfJkt = cnfJkt,
            ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime)
        };
        db.Tokens.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
