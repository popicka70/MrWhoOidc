using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;

namespace MrWhoOidc.Auth.Services;

public interface IKeyRotationService
{
    Task EnsureInitializedAsync(CancellationToken ct = default);
}

internal sealed class KeyRotationService(
    AuthDbContext db,
    IOptions<KeyRotationOptions> options,
    IKeyStore keyStore,
    ITenantAccessor tenantAccessor,
    ILogger<KeyRotationService> logger,
    ISecretProtector? secretProtector = null) : IKeyRotationService
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;

        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");
        var now = DateTimeOffset.UtcNow;

        // Every replica runs this check; the decide+insert must be serialized or two replicas that both see an
        // aged-out key would each insert a new one.
        var rotation = await RunWithRotationLockAsync(
            tenantId,
            token => TryRotateAsync(tenantId, opts, now, token),
            ct).ConfigureAwait(false);

        if (rotation is { } r)
        {
            // Invalidate the cached signing key
            await keyStore.InvalidateActiveSigningKeyCacheAsync(tenantId, ct).ConfigureAwait(false);
            await keyStore.InvalidatePublicJwksCacheAsync(tenantId, ct).ConfigureAwait(false);

            if (r.ForRsaSizeUpgrade)
            {
                logger.LogInformation(
                    "Rotated signing key due to RSA size upgrade. OldKid={OldKid}, OldSizeBits={OldSizeBits}, NewSizeBits={NewSizeBits}, NewKid={Kid}, TenantId={TenantId}",
                    r.OldKid,
                    r.OldRsaKeySizeBits,
                    opts.RsaKeySizeBits,
                    r.NewKid,
                    tenantId);
            }
            else
            {
                logger.LogInformation("Rotated signing key due to age. NewKid={Kid}, TenantId={TenantId}", r.NewKid, tenantId);
            }
        }

        // Retire keys older than RotationInterval + Overlap so they are no longer served
        var retireBefore = now - (opts.RotationInterval + opts.Overlap);
        var oldKeys = await db.SigningKeys
            .Where(k => k.TenantId == tenantId && k.Use == "sig" && k.RetiredAt == null && k.CreatedAt < retireBefore)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (oldKeys.Count > 0)
        {
            foreach (var k in oldKeys)
            {
                k.RetiredAt = now;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Invalidate JWKS cache since retired keys are no longer served
            await keyStore.InvalidatePublicJwksCacheAsync(tenantId, ct).ConfigureAwait(false);

            logger.LogInformation("Retired {Count} old signing keys for TenantId={TenantId}", oldKeys.Count, tenantId);
        }
    }

    private sealed record RotationResult(string OldKid, string NewKid, bool ForRsaSizeUpgrade, int OldRsaKeySizeBits);

    /// <summary>
    /// Runs <paramref name="work"/> while holding a PostgreSQL transaction-scoped advisory lock keyed by
    /// (tenantId, "rotate"), mirroring KeyStore's provisioning lock. Other providers (InMemory/SQLite in tests)
    /// have no cross-process replicas to coordinate, and an ambient transaction already provides the atomicity,
    /// so the work runs directly in those cases.
    /// </summary>
    private async Task<RotationResult?> RunWithRotationLockAsync(
        Guid tenantId,
        Func<CancellationToken, Task<RotationResult?>> work,
        CancellationToken ct)
    {
        if (!string.Equals(db.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal)
            || db.Database.CurrentTransaction is not null)
        {
            return await work(ct).ConfigureAwait(false);
        }

        var lockKey = KeyStore.ComputeAdvisoryLockKey(tenantId, "rotate");
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async (CancellationToken token) =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.ReadCommitted, token).ConfigureAwait(false);

                await db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(@p0)", [lockKey], token).ConfigureAwait(false);

                var result = await work(token).ConfigureAwait(false);
                await tx.CommitAsync(token).ConfigureAwait(false);
                return result;
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the current key (inside the lock, when one is held) and inserts a new one if it is due.
    /// Returns null when no rotation happened.
    /// </summary>
    private async Task<RotationResult?> TryRotateAsync(Guid tenantId, KeyRotationOptions opts, DateTimeOffset now, CancellationToken ct)
    {
        // Find the current active key
        var current = await db.SigningKeys
            .Where(k => k.TenantId == tenantId && k.Use == "sig")
            .OrderByDescending(k => k.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (current is null)
        {
            // No key yet, KeyStore will create on demand; nothing else to do
            return null;
        }

        var rotateForAge = now - current.CreatedAt >= opts.RotationInterval;
        var currentRsaKeySizeBits = 0;
        var rotateForRsaSizeUpgrade = false;
        var currentJwkJson = secretProtector?.UnprotectSigningKeyJwk(current.JwkJson) ?? current.JwkJson;
        if (!rotateForAge && TryGetRsaKeySizeBits(currentJwkJson, out currentRsaKeySizeBits))
        {
            rotateForRsaSizeUpgrade = currentRsaKeySizeBits < opts.RsaKeySizeBits;
        }

        // Rotate when the key aged out or when the configured RSA size was increased beyond the active key.
        if (!rotateForAge && !rotateForRsaSizeUpgrade)
        {
            return null;
        }

        var alg = string.IsNullOrWhiteSpace(opts.SigningAlgorithm) ? SecurityConstants.JwtAlgorithms.RS256 : opts.SigningAlgorithm;
        var kid = Guid.NewGuid().ToString("N");
        string jwkJson;
        string storedAlg;

        if (alg.StartsWith("ES", StringComparison.OrdinalIgnoreCase))
        {
            var curve = alg.ToUpperInvariant() switch
            {
                "ES256" => ECCurve.NamedCurves.nistP256,
                "ES384" => ECCurve.NamedCurves.nistP384,
                "ES512" => ECCurve.NamedCurves.nistP521,
                _ => ECCurve.NamedCurves.nistP256
            };
            using var ecdsa = ECDsa.Create(curve);
            var ecJwk = EcJwk.FromECDsa(ecdsa, kid, alg: alg.ToUpperInvariant(), includePrivate: true);
            jwkJson = ecJwk.ToJson(includePrivate: true);
            storedAlg = ecJwk.Alg;
        }
        else
        {
            using var rsa = RSA.Create(opts.RsaKeySizeBits);
            var rsaJwk = RsaJwk.FromRSA(rsa, kid, alg: alg.ToUpperInvariant(), includePrivate: true);
            jwkJson = rsaJwk.ToJson(includePrivate: true);
            storedAlg = rsaJwk.Alg;
        }

        db.SigningKeys.Add(new SigningKey
        {
            Kid = kid,
            Use = "sig",
            Alg = storedAlg,
            JwkJson = secretProtector?.ProtectSigningKeyJwk(jwkJson) ?? jwkJson, // never persist plaintext private keys
            CreatedAt = now,
            TenantId = tenantId
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new RotationResult(current.Kid, kid, rotateForRsaSizeUpgrade, currentRsaKeySizeBits);
    }

    private static bool TryGetRsaKeySizeBits(string jwkJson, out int keySizeBits)
    {
        keySizeBits = 0;

        try
        {
            var jwk = new JsonWebKey(jwkJson);
            if (!string.Equals(jwk.Kty, "RSA", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(jwk.N))
            {
                return false;
            }

            keySizeBits = Base64UrlEncoder.DecodeBytes(jwk.N).Length * 8;
            return keySizeBits > 0;
        }
        catch
        {
            return false;
        }
    }
}
