using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Crypto;
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
    ILogger<KeyRotationService> logger) : IKeyRotationService
{
    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;

        // Find the current active key
        var current = await db.SigningKeys.OrderByDescending(k => k.CreatedAt).FirstOrDefaultAsync(ct);
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

            db.SigningKeys.Add(new SigningKey
            {
                Kid = jwk.Kid,
                Alg = jwk.Alg,
                JwkJson = jwk.ToJson(includePrivate: true),
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Rotated signing key. New kid={Kid}", kid);
        }

        // Retire keys older than RotationInterval + Overlap so they are no longer served
        var retireBefore = DateTimeOffset.UtcNow - (opts.RotationInterval + opts.Overlap);
        var oldKeys = await db.SigningKeys.Where(k => k.RetiredAt == null && k.CreatedAt < retireBefore).ToListAsync(ct);
        if (oldKeys.Count > 0)
        {
            foreach (var k in oldKeys)
            {
                k.RetiredAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Retired {Count} old signing keys", oldKeys.Count);
        }
    }
}
