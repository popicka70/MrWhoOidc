using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Implementation of IRefreshTokenExchanger that extracts logic from TokenService.
/// </summary>
public sealed class RefreshTokenExchanger(
    AuthDbContext db,
    IJwtService jwt,
    IRefreshTokenService refreshTokens,
    IRevocationService revocations,
    IOptions<AuthOptions> authOptions,
    ITenantSettingsService settingsService,
    IEntitlementsProvider entitlementsProvider,
    ITenantsClaimService tenantsClaimService,
    IPairwiseSubjectService pairwiseSubjectService,
    IAccessTokenClaimBuilder claimBuilder,
    ITokenLifetimeResolver lifetimeResolver,
    IOpaqueTokenPolicy opaquePolicy) : IRefreshTokenExchanger
{
    private static readonly JsonSerializerOptions EntitlementsJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(bool ok, object? payload, string? error, int status)> ExchangeAsync(RefreshTokenExchangeRequest request, CancellationToken ct = default)
    {
        var hash = CryptoHelper.ComputeSha256Base64(request.RefreshToken);
        var tokenEntity = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Type == "refresh", ct).ConfigureAwait(false);
        var settings = await settingsService.GetCurrentTenantSettingsAsync().ConfigureAwait(false) ?? new Auth.Settings.TenantSettings();
        var absoluteLifetimeSeconds = settings?.Tokens?.RefreshTokenAbsoluteLifetimeSeconds ?? 2592000; // Default: 30 days
        var absoluteLifetime = TimeSpan.FromSeconds(absoluteLifetimeSeconds);
        var absoluteExpired = tokenEntity is not null && tokenEntity.CreatedAt.Add(absoluteLifetime) < DateTimeOffset.UtcNow;

        if (tokenEntity is null || tokenEntity.ExpiresAt < DateTimeOffset.UtcNow || absoluteExpired || !string.Equals(tokenEntity.ClientId, request.ClientId, StringComparison.Ordinal))
        {
            return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
        }

        if (tokenEntity.RevokedAt != null)
        {
            await revocations.RevokeRefreshTokenFamilyAsync(tokenEntity.Id, ct).ConfigureAwait(false);
            return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
        }

        if (!string.IsNullOrEmpty(tokenEntity.CnfJkt) && !string.Equals(tokenEntity.CnfJkt, request.DpopJkt, StringComparison.Ordinal))
        {
            return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
        }

        // The grant this refresh token belongs to. Rows from before FamilyId existed fall back to their own id
        // (RevocationService also matches Id == familyId, so the legacy parent stays covered).
        var familyId = tokenEntity.FamilyId ?? tokenEntity.Id;

        var scopes = JsonSerializer.Deserialize<string[]>(tokenEntity.ScopesJson) ?? Array.Empty<string>();
        // The audience the grant was issued for stays with the refresh token. A refresh request may name it again
        // but not switch to another resource (RFC 8707 §2.2; the C9 refresh gap), and without a resource it keeps
        // the original audience instead of the default one (an admin-API token stays one, ADR-0010). Refresh tokens
        // issued before the audience was recorded keep the old behaviour.
        if (!string.IsNullOrWhiteSpace(tokenEntity.Audience)
            && !string.IsNullOrWhiteSpace(request.Resource)
            && !string.Equals(tokenEntity.Audience, request.Resource, StringComparison.Ordinal))
        {
            return (false,
                new { error = OAuthConstants.ErrorCodes.InvalidTarget, error_description = "resource was not authorized for this refresh token" },
                OAuthConstants.ErrorCodes.InvalidTarget,
                400);
        }

        var audience = !string.IsNullOrWhiteSpace(request.Resource)
            ? request.Resource
            : !string.IsNullOrWhiteSpace(tokenEntity.Audience)
                ? tokenEntity.Audience
                : (authOptions.Value.ApiAudiences?.FirstOrDefault()) ?? "api";

        var userForTenant = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == tokenEntity.UserId, ct).ConfigureAwait(false);

        if (userForTenant is { Status: UserStatus.Deactivated })
        {
            return (false, new { error = "invalid_grant" }, "User account has been deactivated.", 403);
        }

        Guid? tenantIdForEntitlements = request.TenantId ?? userForTenant?.TenantId;
        if (tenantIdForEntitlements == Guid.Empty) tenantIdForEntitlements = null;

        var (scopesFiltered, entitlementsClaimJson, signedLicenseTokens) = await ApplyProductEntitlementsAsync(
            subjectId: tokenEntity.UserId.ToString(),
            tenantId: tenantIdForEntitlements,
            requestedScopes: scopes,
            issuer: request.Issuer,
            ct: ct).ConfigureAwait(false);
        scopes = scopesFiltered;

        string? tenantsClaimJson = null;
        if (scopes.Contains(OidcConstants.Scopes.Tenants, StringComparer.Ordinal))
        {
            tenantsClaimJson = await tenantsClaimService.BuildTenantsClaimJsonAsync(tokenEntity.UserId, ct).ConfigureAwait(false);
        }

        var opaqueEnabled = opaquePolicy.ShouldUseOpaqueAccessToken(audience);

        var clientContext = await db.Clients
            .AsNoTracking()
            .Where(c => c.ClientId == request.ClientId)
            .Select(c => new
            {
                Client = c,
                RealmName = db.Realms
                    .AsNoTracking()
                    .Where(r => r.Id == c.RealmId)
                    .Select(r => r.Name)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var client = clientContext?.Client;
        var subject = tokenEntity.UserId.ToString();
        if (client is not null)
        {
            subject = await pairwiseSubjectService.GetSubjectAsync(client, tokenEntity.UserId, ct).ConfigureAwait(false);
        }
        var realmName = clientContext?.RealmName;
        string[] roleNames = Array.Empty<string>();
        if (client is not null)
        {
            if (scopes.Contains("roles"))
            {
                var realmRoleNamesQuery = db.UserRealmRoleAssignments.AsNoTracking()
                    .Where(a => a.UserId == tokenEntity.UserId && a.RealmId == client.RealmId && a.IsActive)
                    .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
                    .Where(x => x.r.IsActive)
                    .Select(x => x.r.Name);

                var clientRoleNamesQuery = db.UserClientRoleAssignments.AsNoTracking()
                    .Where(a => a.UserId == tokenEntity.UserId && a.ClientId == client.Id && a.IsActive)
                    .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
                    .Where(x => x.r.IsActive)
                    .Select(x => x.r.Name);

                roleNames = await realmRoleNamesQuery.Union(clientRoleNamesQuery).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
            }
        }

        var accessTokenLifetime = lifetimeResolver.ResolveAccessTokenLifetime(client!, settings!);

        // The access token is minted here but its row is only written inside the rotation transaction below,
        // after this request has won the claim on the refresh token.
        string accessToken;
        Persistence.Token accessTokenRow;
        if (opaqueEnabled)
        {
            var jti = Guid.NewGuid().ToString("N");
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            accessTokenRow = BuildAccessTokenRow(tokenEntity.UserId, request.ClientId, audience, scopes, jti, raw, accessTokenLifetime, request.DpopJkt, tokenEntity.TenantId, request.IpAddress, request.UserAgent);
            accessToken = raw;
        }
        else
        {
            var claimRequest = new AccessTokenClaimRequest(
                UserId: tokenEntity.UserId,
                ClientId: request.ClientId,
                Scopes: scopes,
                Issuer: request.Issuer,
                DpopJkt: request.DpopJkt,
                EntitlementsClaimJson: entitlementsClaimJson,
                TenantsClaimJson: tenantsClaimJson,
                RealmName: realmName,
                RoleNames: roleNames,
                TenantId: tenantIdForEntitlements,
                Subject: subject
            );

            var accessClaims = await claimBuilder.BuildClaimsAsync(claimRequest, ct).ConfigureAwait(false);
            var claimsList = accessClaims.ToList();
            var accessTokenJti = claimsList
                .FirstOrDefault(c => string.Equals(c.Type, JwtRegisteredClaimNames.Jti, StringComparison.Ordinal)
                    || string.Equals(c.Type, "jti", StringComparison.Ordinal))
                ?.Value;

            if (signedLicenseTokens is { Count: > 0 })
            {
                var licenseJson = JsonSerializer.Serialize(signedLicenseTokens, EntitlementsJsonOptions);
                claimsList.Add(new System.Security.Claims.Claim("license", licenseJson));
            }

            accessToken = await jwt.CreateJwtAsync(request.Issuer, audience, claimsList, DateTimeOffset.UtcNow.Add(accessTokenLifetime), tokenType: SecurityConstants.JwtTokenTypes.AtJwt, ct: ct).ConfigureAwait(false);
            accessTokenRow = BuildAccessTokenRow(tokenEntity.UserId, request.ClientId, audience, scopes, accessTokenJti, accessToken, accessTokenLifetime, request.DpopJkt, tokenEntity.TenantId, request.IpAddress, request.UserAgent);
        }

        // Claim the presented refresh token, record the new access token and insert the child refresh token in ONE
        // transaction that also holds the family lock (PostgreSQL). Two consequences:
        // - concurrent requests with the same token: the claim (conditional UPDATE) lets one win; the loser's claim
        //   blocks on the winner's row lock until it commits, so the loser's family revocation sees the child;
        // - the child carries the family id from the moment it is inserted, so a family revocation never depends on
        //   a later "link" step having run (the old ReplacedById walk missed children that were not yet linked).
        var newRefresh = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await RefreshTokenFamilyLock.AcquireAsync(db, familyId, ct).ConfigureAwait(false);

            if (!await TryClaimAsync(tokenEntity, ct).ConfigureAwait(false))
            {
                // Another request already claimed (or a revocation already revoked) this token: reuse.
                return null;
            }

            // Issued together with the child refresh token: same family, revoked with it (RFC 7009 §2.1).
            accessTokenRow.FamilyId = familyId;
            db.Tokens.Add(accessTokenRow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var (newRefreshInner, _) = await refreshTokens.CreateRefreshTokenAsync(
                tokenEntity.UserId,
                request.ClientId,
                scopes,
                request.IpAddress,
                request.UserAgent,
                ct,
                familyCreatedAt: tokenEntity.CreatedAt,
                cnfJkt: request.DpopJkt,
                audience: audience).ConfigureAwait(false);

            var newRefreshHash = CryptoHelper.ComputeSha256Base64(newRefreshInner);
            var newTokenEntity = await db.Tokens
                .FirstOrDefaultAsync(
                    t => t.TokenHash == newRefreshHash && t.Type == "refresh" && t.ClientId == request.ClientId && t.UserId == tokenEntity.UserId,
                    ct)
                .ConfigureAwait(false);
            if (newTokenEntity is not null)
            {
                // The child joins the parent's family (it was created as its own family). ReplacedById keeps the
                // parent link for lineage and for instances that still walk it.
                newTokenEntity.FamilyId = familyId;
                newTokenEntity.ReplacedById = tokenEntity.Id;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return newRefreshInner;
        });

        if (newRefresh is null)
        {
            await revocations.RevokeRefreshTokenFamilyAsync(tokenEntity.Id, ct).ConfigureAwait(false);
            return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
        }

        var newRefreshResult = newRefresh;

        var payload = new
        {
            access_token = accessToken,
            refresh_token = newRefreshResult,
            token_type = !string.IsNullOrEmpty(request.DpopJkt) ? "DPoP" : OAuthConstants.TokenTypes.Bearer,
            expires_in = (int)accessTokenLifetime.TotalSeconds,
            scope = string.Join(' ', scopes)
        };
        return (true, payload, null, 200);
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
                var req = new Entitlements.Contracts.SignedLicenseTokenRequest
                {
                    SubjectId = subjectId,
                    ProductKey = productKey,
                    TenantId = tenantId
                };

                var result = await client.GetSignedLicenseTokenAsync(req, issuer, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Marks the presented refresh token as used. Returns false when it was already used or revoked.
    /// </summary>
    private async Task<bool> TryClaimAsync(Persistence.Token tokenEntity, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            var claimed = await db.Tokens
                .Where(t => t.Id == tokenEntity.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);
            return claimed == 1;
        }

        // In-memory provider (tests): no ExecuteUpdate; the tracked entity is the claim.
        if (tokenEntity.RevokedAt is not null)
        {
            return false;
        }

        tokenEntity.RevokedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private static Persistence.Token BuildAccessTokenRow(Guid userId, string clientId, string audience, string[] scopes, string? jti, string rawToken, TimeSpan lifetime, string? cnfJkt, Guid tenantId, string? ipAddress, string? userAgent)
    {
        return new Persistence.Token
        {
            Type = "access",
            TokenHash = CryptoHelper.ComputeSha256Base64(rawToken),
            UserId = userId,
            ClientId = clientId,
            TenantId = tenantId,
            ScopesJson = JsonSerializer.Serialize(scopes),
            Audience = audience,
            Jti = jti,
            CnfJkt = cnfJkt,
            ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime),
            IpAddress = ipAddress,
            UserAgent = userAgent
        };
    }
}

