using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
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

internal sealed class KeyStore(AuthDbContext db, ITenantAccessor tenantAccessor, HybridCache cache, IOptions<KeyRotationOptions> keyRotationOptions) : IKeyStore
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
                var current = await db.SigningKeys
                    .Where(k => k.TenantId == tenantId && k.Use == "sig")
                    .OrderByDescending(k => k.CreatedAt)
                    .FirstOrDefaultAsync(cancel)
                    .ConfigureAwait(false);

                if (current is null)
                {
                    var (jwkJson, kid, alg) = GeneratePrivateSigningJwkJson(keyRotationOptions.Value.SigningAlgorithm);

                    db.SigningKeys.Add(new Persistence.SigningKey
                    {
                        Kid = kid,
                        Use = "sig",
                        Alg = alg,
                        JwkJson = jwkJson,
                        TenantId = tenantId
                    });
                    await db.SaveChangesAsync(cancel).ConfigureAwait(false);
                    return new JsonWebKey(jwkJson);
                }

                // Load from DB
                return new JsonWebKey(current.JwkJson);
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
                var current = await db.SigningKeys
                    .Where(k => k.TenantId == tenantId && k.Use == "enc")
                    .OrderByDescending(k => k.CreatedAt)
                    .FirstOrDefaultAsync(cancel)
                    .ConfigureAwait(false);

                if (current is null)
                {
                    var (jwkJson, kid, alg) = GeneratePrivateEncryptionJwkJson();

                    db.SigningKeys.Add(new Persistence.SigningKey
                    {
                        Kid = kid,
                        Use = "enc",
                        Alg = alg,
                        JwkJson = jwkJson,
                        TenantId = tenantId
                    });
                    await db.SaveChangesAsync(cancel).ConfigureAwait(false);

                    // Invalidate public JWKS cache so the new enc key is published immediately
                    await InvalidatePublicJwksCacheAsync(tenantId, cancel).ConfigureAwait(false);

                    return new JsonWebKey(jwkJson);
                }

                return new JsonWebKey(current.JwkJson);
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
                    var jwk = new JsonWebKey(k.JwkJson);
                    result.Add(StripPrivateKeyMaterial(jwk));
                }
                return result;
            },
            options,
            tags,
            ct
        ).ConfigureAwait(false);
    }

    private static (string jwkJson, string kid, string alg) GeneratePrivateSigningJwkJson(string? configuredAlg)
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
        using var rsa = RSA.Create(2048);
        var rsaJwk = RsaJwk.FromRSA(rsa, kid, alg: alg.ToUpperInvariant(), includePrivate: true, use: "sig");
        return (rsaJwk.ToJson(includePrivate: true), rsaJwk.Kid, rsaJwk.Alg);
    }

    private static (string jwkJson, string kid, string alg) GeneratePrivateEncryptionJwkJson()
    {
        var kid = Guid.NewGuid().ToString("N");
        using var rsa = RSA.Create(2048);
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
