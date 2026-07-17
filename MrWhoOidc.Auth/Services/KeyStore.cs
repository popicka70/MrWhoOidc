using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using System.Security.Cryptography;
using System.Text.Json;

namespace MrWhoOidc.Auth.Services;

public interface IKeyStore
{
    Task<JsonWebKey> GetActiveSigningKeyAsync(CancellationToken ct = default);
    Task<JsonWebKey> GetActiveEncryptionKeyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<JsonWebKey>> GetPublicJwksAsync(bool includeEncryptionKeys = false, CancellationToken ct = default);
    Task InvalidateActiveSigningKeyCacheAsync(Guid tenantId, CancellationToken ct = default);
    Task InvalidateActiveEncryptionKeyCacheAsync(Guid tenantId, CancellationToken ct = default);
    Task InvalidatePublicJwksCacheAsync(Guid tenantId, CancellationToken ct = default);
}

internal sealed class KeyStore(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    HybridCache cache,
    IOptions<KeyRotationOptions> keyRotationOptions,
    ILogger<KeyStore>? logger = null,
    ISecretProtector? secretProtector = null) : IKeyStore
{
    public async Task<JsonWebKey> GetActiveSigningKeyAsync(CancellationToken ct = default)
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
                // Order by Id (UUIDv7, monotonic) as a deterministic tiebreaker so
                // concurrent first-hit inserts converge on the same canonical key
                // even when CreatedAt ties at the millisecond boundary.
                var current = await db.SigningKeys
                    .Where(k => k.TenantId == tenantId && k.Use == "sig")
                    .OrderByDescending(k => k.CreatedAt)
                    .ThenByDescending(k => k.Id)
                    .FirstOrDefaultAsync(cancel)
                    .ConfigureAwait(false);

                if (current is null)
                {
                    // Serialize initial key provisioning via a PostgreSQL transaction-scoped
                    // advisory lock so only one request inserts a key; concurrent callers
                    // block on the lock and then find the key already present.
                    current = await ProvisionKeyWithAdvisoryLockAsync(
                        tenantId, "sig", cancel,
                        () =>
                        {
                            var rotationOptions = keyRotationOptions.Value;
                            var (jwkJson, kid, alg) = GeneratePrivateSigningJwkJson(
                                rotationOptions.SigningAlgorithm, rotationOptions.RsaKeySizeBits);
                            return (jwkJson, kid, alg);
                        }).ConfigureAwait(false);
                }

                if (current is null)
                {
                    throw new InvalidOperationException(
                        $"No signing key found for tenant {tenantId} after provisioning attempt.");
                }

                var loadedJwkJson = UnprotectSigningKeyJwk(current.JwkJson);
                await ProtectSigningKeyIfNeededAsync(current, loadedJwkJson, cancel).ConfigureAwait(false);
                return new JsonWebKey(loadedJwkJson);
            },
            options,
            tags,
            ct
        ).ConfigureAwait(false);
    }

    public async Task<JsonWebKey> GetActiveEncryptionKeyAsync(CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        var cacheKey = $"enc:key:active:{tenantId}";
        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(30),
            LocalCacheExpiration = TimeSpan.FromMinutes(10)
        };
        var tags = new[] { "signing-keys", $"tenant:{tenantId}" };

        return await cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                // Order by Id (UUIDv7, monotonic) as a deterministic tiebreaker so
                // concurrent first-hit inserts converge on the same canonical key
                // even when CreatedAt ties at the millisecond boundary.
                var current = await db.SigningKeys
                    .Where(k => k.TenantId == tenantId && k.Use == "enc")
                    .OrderByDescending(k => k.CreatedAt)
                    .ThenByDescending(k => k.Id)
                    .FirstOrDefaultAsync(cancel)
                    .ConfigureAwait(false);

                var createdNewKey = false;
                if (current is null)
                {
                    // Serialize initial key provisioning via a PostgreSQL transaction-scoped
                    // advisory lock so only one request inserts a key; concurrent callers
                    // block on the lock and then find the key already present.
                    current = await ProvisionKeyWithAdvisoryLockAsync(
                        tenantId, "enc", cancel,
                        () =>
                        {
                            var (jwkJson, kid, alg) = GeneratePrivateEncryptionJwkJson(
                                keyRotationOptions.Value.RsaKeySizeBits);
                            return (jwkJson, kid, alg);
                        }).ConfigureAwait(false);
                    createdNewKey = true;
                }

                // Invalidate public JWKS cache so a newly created enc key is published immediately
                if (createdNewKey)
                {
                    await InvalidatePublicJwksCacheAsync(tenantId, cancel).ConfigureAwait(false);
                }

                if (current is null)
                {
                    throw new InvalidOperationException(
                        $"No encryption key found for tenant {tenantId} after provisioning attempt.");
                }

                var loadedEncJwkJson = UnprotectSigningKeyJwk(current.JwkJson);
                await ProtectSigningKeyIfNeededAsync(current, loadedEncJwkJson, cancel).ConfigureAwait(false);
                return new JsonWebKey(loadedEncJwkJson);
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

    public async Task InvalidateActiveEncryptionKeyCacheAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cacheKey = $"enc:key:active:{tenantId}";
        await cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
    }

    public async Task InvalidatePublicJwksCacheAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cacheKeySigOnly = $"signing:jwks:public:{tenantId}:enc:false";
        var cacheKeyWithEnc = $"signing:jwks:public:{tenantId}:enc:true";
        await cache.RemoveAsync(cacheKeySigOnly, ct).ConfigureAwait(false);
        await cache.RemoveAsync(cacheKeyWithEnc, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JsonWebKey>> GetPublicJwksAsync(bool includeEncryptionKeys = false, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        var cacheKey = $"signing:jwks:public:{tenantId}:enc:{includeEncryptionKeys.ToString().ToLowerInvariant()}";
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
                if (includeEncryptionKeys)
                {
                    // Ensure an enc key exists so JWKS remains consistent when encryption is enabled.
                    _ = await GetActiveEncryptionKeyAsync(cancel).ConfigureAwait(false);
                }

                // Publish active and non-retired previous keys; hide retired keys
                var keys = await db.SigningKeys
                    .Where(k => k.RetiredAt == null && k.TenantId == tenantId && (k.Use == "sig" || (includeEncryptionKeys && k.Use == "enc")))
                    .OrderByDescending(k => k.CreatedAt)
                    .ToListAsync(cancel)
                    .ConfigureAwait(false);

                var result = new List<JsonWebKey>(capacity: keys.Count);
                foreach (var k in keys)
                {
                    var jwk = new JsonWebKey(UnprotectSigningKeyJwk(k.JwkJson));
                    result.Add(StripPrivateKeyMaterial(jwk));
                }
                return result;
            },
            options,
            tags,
            ct
        ).ConfigureAwait(false);
    }

    private async Task<SigningKey> ProvisionKeyWithAdvisoryLockAsync(
        Guid tenantId, string use, CancellationToken ct,
        Func<(string jwkJson, string kid, string alg)> generateKey)
    {
        // The InMemory provider (used by unit tests) does not support raw SQL or
        // transactions. Fall back to a simple check-insert-requery path there.
        var provider = db.Database.ProviderName;
        if (string.Equals(provider, "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal))
        {
            return await ProvisionKeySimpleAsync(tenantId, use, ct, generateKey).ConfigureAwait(false);
        }

        // PostgreSQL: use a transaction-scoped advisory lock keyed on a stable hash
        // of (tenantId, use) so only one request provisions the initial key; all
        // concurrent callers block until the lock holder commits, then re-query and
        // find the key already present. The lock is automatically released when the
        // transaction commits or rolls back.
        const string lockSql = "SELECT pg_advisory_xact_lock(@p0)";

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        var lockKey = ComputeAdvisoryLockKey(tenantId, use);
        await db.Database.ExecuteSqlRawAsync(lockSql, lockKey, ct).ConfigureAwait(false);

        // Re-check inside the lock: the first caller to win the lock inserts the key;
        // everyone else finds it already present and skips the insert.
        var existing = await db.SigningKeys
            .Where(k => k.TenantId == tenantId && k.Use == use)
            .OrderByDescending(k => k.CreatedAt)
            .ThenByDescending(k => k.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // Another caller already provisioned the key while we waited on the lock.
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return existing;
        }

        var (jwkJson, kid, alg) = generateKey();

        var key = new SigningKey
        {
            Kid = kid,
            Use = use,
            Alg = alg,
            JwkJson = jwkJson,
            TenantId = tenantId
        };
        db.SigningKeys.Add(key);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);

        // Re-query to return a detached entity consistent with the cache factory contract.
        var canonical = await db.SigningKeys
            .Where(k => k.TenantId == tenantId && k.Use == use)
            .OrderByDescending(k => k.CreatedAt)
            .ThenByDescending(k => k.Id)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (canonical is null)
        {
            throw new InvalidOperationException(
                $"No {use} key found for tenant {tenantId} immediately after provisioning.");
        }

        logger?.LogInformation("[KeyStore] Provisioned initial {Use} key {Kid} for tenant {TenantId}", use, canonical.Kid, tenantId);
        return canonical;
    }

    /// <summary>
    /// Fallback provisioning path for providers that don't support raw SQL / advisory
    /// locks (e.g., EF Core InMemory used in unit tests). Does not guard against
    /// stampedes but is sufficient for single-threaded test scenarios.
    /// </summary>
    private async Task<SigningKey> ProvisionKeySimpleAsync(
        Guid tenantId, string use, CancellationToken ct,
        Func<(string jwkJson, string kid, string alg)> generateKey)
    {
        var (jwkJson, kid, alg) = generateKey();

        db.SigningKeys.Add(new SigningKey
        {
            Kid = kid,
            Use = use,
            Alg = alg,
            JwkJson = jwkJson,
            TenantId = tenantId
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var canonical = await db.SigningKeys
            .Where(k => k.TenantId == tenantId && k.Use == use)
            .OrderByDescending(k => k.CreatedAt)
            .ThenByDescending(k => k.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return canonical
            ?? throw new InvalidOperationException(
                $"No {use} key found for tenant {tenantId} after provisioning.");
    }

    private static long ComputeAdvisoryLockKey(Guid tenantId, string use)
    {
        // Combine tenantId bytes with use-hash into a stable int64.
        var useHash = (long)unchecked((uint)StringComparer.Ordinal.GetHashCode(use));
        var tenantHash = (long)(tenantId.GetHashCode() & 0xFFFFFFFF);
        return (tenantHash << 32) | (useHash & 0xFFFFFFFF);
    }

    private static (string jwkJson, string kid, string alg) GeneratePrivateSigningJwkJson(string? configuredAlg, int rsaKeySizeBits)
    {
        var alg = string.IsNullOrWhiteSpace(configuredAlg) ? SecurityConstants.JwtAlgorithms.RS256 : configuredAlg;
        var kid = Guid.NewGuid().ToString("N");

        if (alg.StartsWith("ES", StringComparison.OrdinalIgnoreCase))
        {
            var curve = alg.ToUpperInvariant() switch
            {
                "ES256" => ECCurve.NamedCurves.nistP256,
                "ES384" => ECCurve.NamedCurves.nistP384,
                // ES512 uses P-521 per JWA
                "ES512" => ECCurve.NamedCurves.nistP521,
                _ => ECCurve.NamedCurves.nistP256
            };

            using var ecdsa = ECDsa.Create(curve);
            var jwk = EcJwk.FromECDsa(ecdsa, kid, alg: alg.ToUpperInvariant(), includePrivate: true);
            return (jwk.ToJson(includePrivate: true), jwk.Kid, jwk.Alg);
        }

        // Default to RSA for RS*/PS*
        using var rsa = RSA.Create(rsaKeySizeBits);
        var rsaJwk = RsaJwk.FromRSA(rsa, kid, alg: alg.ToUpperInvariant(), includePrivate: true, use: "sig");
        return (rsaJwk.ToJson(includePrivate: true), rsaJwk.Kid, rsaJwk.Alg);
    }

    private string UnprotectSigningKeyJwk(string storedJwkJson)
        => secretProtector?.UnprotectSigningKeyJwk(storedJwkJson) ?? storedJwkJson;

    private async Task ProtectSigningKeyIfNeededAsync(SigningKey key, string plaintextJwkJson, CancellationToken ct)
    {
        if (secretProtector is null || secretProtector.IsProtected(key.JwkJson))
        {
            return;
        }

        key.JwkJson = secretProtector.ProtectSigningKeyJwk(plaintextJwkJson);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static (string jwkJson, string kid, string alg) GeneratePrivateEncryptionJwkJson(int rsaKeySizeBits)
    {
        var kid = Guid.NewGuid().ToString("N");
        using var rsa = RSA.Create(rsaKeySizeBits);
        var rsaJwk = RsaJwk.FromRSA(rsa, kid, alg: "RSA-OAEP", includePrivate: true, use: "enc");
        return (rsaJwk.ToJson(includePrivate: true), rsaJwk.Kid, rsaJwk.Alg);
    }

    private static JsonWebKey StripPrivateKeyMaterial(JsonWebKey jwk)
    {
        // Build a copy so we don't accidentally mutate cached instances.
        // Preserve: kty/kid/alg/use + public parameters.
        if (string.Equals(jwk.Kty, "EC", StringComparison.OrdinalIgnoreCase))
        {
            var pub = new Dictionary<string, object?>
            {
                ["kty"] = "EC",
                ["kid"] = jwk.Kid,
                ["alg"] = jwk.Alg,
                ["use"] = jwk.Use,
                ["crv"] = jwk.Crv,
                ["x"] = jwk.X,
                ["y"] = jwk.Y
            };
            var pubJson = JsonSerializer.Serialize(pub, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            return new JsonWebKey(pubJson);
        }

        // RSA
        var rsaPub = new Dictionary<string, object?>
        {
            ["kty"] = "RSA",
            ["kid"] = jwk.Kid,
            ["alg"] = jwk.Alg,
            ["use"] = jwk.Use,
            ["n"] = jwk.N,
            ["e"] = jwk.E
        };
        var rsaJson = JsonSerializer.Serialize(rsaPub, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        return new JsonWebKey(rsaJson);
    }
}
