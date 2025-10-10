using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
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
    ILogger<KeyRotationService> logger) : IKeyRotationService
{
    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;

        // Find the current active key
        var current = await db.SigningKeys.OrderByDescending(k => k.CreatedAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (current is null)
        {
            // No key yet, KeyStore will create on demand; nothing else to do
            return;
        }

        // If current key is older than RotationInterval, generate a new one and keep the old (not retired yet)
        if (DateTimeOffset.UtcNow - current.CreatedAt >= opts.RotationInterval)
        {
            using var rsa = RSA.Create(2048);
            var kid = Guid.NewGuid().ToString("N");
            var jwk = RsaJwk.FromRSA(rsa, kid, alg: "RS256", includePrivate: true);

            var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

            db.SigningKeys.Add(new SigningKey
            {
                Kid = jwk.Kid,
                Alg = jwk.Alg,
                JwkJson = jwk.ToJson(includePrivate: true),
                CreatedAt = DateTimeOffset.UtcNow,
                TenantId = tenantId
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            
            // Invalidate the cached signing key
            await keyStore.InvalidateActiveSigningKeyCacheAsync(tenantId, ct).ConfigureAwait(false);
            await keyStore.InvalidatePublicJwksCacheAsync(tenantId, ct).ConfigureAwait(false);
            
            logger.LogInformation("Rotated signing key. New kid={Kid}, TenantId={TenantId}", kid, tenantId);
        }

        // Retire keys older than RotationInterval + Overlap so they are no longer served
        var retireBefore = DateTimeOffset.UtcNow - (opts.RotationInterval + opts.Overlap);
        var oldKeys = await db.SigningKeys.Where(k => k.RetiredAt == null && k.CreatedAt < retireBefore).ToListAsync(ct).ConfigureAwait(false);
        if (oldKeys.Count > 0)
        {
            var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");
            
            foreach (var k in oldKeys)
            {
                k.RetiredAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            
            // Invalidate JWKS cache since retired keys are no longer served
            await keyStore.InvalidatePublicJwksCacheAsync(tenantId, ct).ConfigureAwait(false);
            
            logger.LogInformation("Retired {Count} old signing keys for TenantId={TenantId}", oldKeys.Count, tenantId);
        }
    }
}
