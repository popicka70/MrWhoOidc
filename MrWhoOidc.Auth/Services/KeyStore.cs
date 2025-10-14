using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IKeyStore
{
    Task<RsaJwk> GetActiveSigningKeyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RsaJwk>> GetPublicJwksAsync(CancellationToken ct = default);
    Task InvalidateActiveSigningKeyCacheAsync(Guid tenantId, CancellationToken ct = default);
    Task InvalidatePublicJwksCacheAsync(Guid tenantId, CancellationToken ct = default);
}

internal sealed class KeyStore(AuthDbContext db, ITenantAccessor tenantAccessor, HybridCache cache) : IKeyStore
{
    public async Task<RsaJwk> GetActiveSigningKeyAsync(CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");
        
        var cacheKey = $"signing:key:active:{tenantId}";
        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(30),         // L2 (Redis)
            LocalCacheExpiration = TimeSpan.FromMinutes(10) // L1 (memory)
        };
        var tags = new[] { "signing-keys", $"tenant:{tenantId}" };

        return await cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                var current = await db.SigningKeys
                    .Where(k => k.TenantId == tenantId)
                    .OrderByDescending(k => k.CreatedAt)
                    .FirstOrDefaultAsync(cancel)
                    .ConfigureAwait(false);
                    
                if (current is null)
                {
                    // Generate a new RSA keypair and persist it
                    using var rsa = RSA.Create(2048);
                    var kid = Guid.NewGuid().ToString("N");
                    var jwk = RsaJwk.FromRSA(rsa, kid, alg: "RS256", includePrivate: true);

                    db.SigningKeys.Add(new Persistence.SigningKey
                    {
                        Kid = jwk.Kid,
                        Alg = jwk.Alg,
                        JwkJson = jwk.ToJson(includePrivate: true),
                        TenantId = tenantId
                    });
                    await db.SaveChangesAsync(cancel).ConfigureAwait(false);
                    return jwk;
                }

                // Load from DB
                var stored = System.Text.Json.JsonSerializer.Deserialize<RsaJwk>(current.JwkJson)!;
                return stored;
            },
            options,
            tags,
            ct
        ).ConfigureAwait(false);
    }

    public async Task InvalidateActiveSigningKeyCacheAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cacheKey = $"signing:key:active:{tenantId}";
        await cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
    }

    public async Task InvalidatePublicJwksCacheAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cacheKey = $"signing:jwks:public:{tenantId}";
        await cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RsaJwk>> GetPublicJwksAsync(CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");
        
        var cacheKey = $"signing:jwks:public:{tenantId}";
        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(30),         // L2 (Redis)
            LocalCacheExpiration = TimeSpan.FromMinutes(10) // L1 (memory)
        };
        var tags = new[] { "signing-keys", $"tenant:{tenantId}" };

        return await cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                // Publish active and non-retired previous keys; hide retired keys
                var keys = await db.SigningKeys
                    .Where(k => k.RetiredAt == null && k.TenantId == tenantId)
                    .OrderByDescending(k => k.CreatedAt)
                    .ToListAsync(cancel)
                    .ConfigureAwait(false);

                return keys
                    .Select(k => System.Text.Json.JsonSerializer.Deserialize<RsaJwk>(k.JwkJson)!)
                    .Select(k => new RsaJwk
                    {
                        Kty = k.Kty,
                        Kid = k.Kid,
                        Alg = k.Alg,
                        Use = k.Use,
                        N = k.N,
                        E = k.E,
                        D = null,
                        P = null,
                        Q = null,
                        DP = null,
                        DQ = null,
                        QI = null
                    })
                    .ToList() as IReadOnlyList<RsaJwk>;
            },
            options,
            tags,
            ct
        ).ConfigureAwait(false);
    }
}
