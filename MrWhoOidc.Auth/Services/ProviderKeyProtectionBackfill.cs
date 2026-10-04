using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// One-off, idempotent: protects upstream-IdP keys (<see cref="IdentityProviderKey.Jwk"/>) still stored in plaintext.
/// New and edited keys are protected by <see cref="AuthDbContext"/> on save; this covers rows written before that.
/// </summary>
public static class ProviderKeyProtectionBackfill
{
    public static async Task<int> RunAsync(AuthDbContext db, ISecretProtector protector, ILogger logger, CancellationToken ct = default)
    {
        var plaintext = await db.IdentityProviderKeys
            .IgnoreQueryFilters()
            .Where(k => k.Jwk != "" && !k.Jwk.StartsWith("dp:v1:"))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (plaintext.Count == 0)
        {
            return 0;
        }

        // Marking the rows modified is enough: SaveChanges protects IdentityProviderKey.Jwk.
        foreach (var key in plaintext)
        {
            db.Entry(key).Property(k => k.Jwk).IsModified = true;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Protected {Count} upstream identity provider key(s) that were stored in plaintext", plaintext.Count);
        return plaintext.Count;
    }
}
