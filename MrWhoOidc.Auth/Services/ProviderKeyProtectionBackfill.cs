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

/// <summary>
/// One-off, idempotent: protects upstream client secrets inside <see cref="IdentityProvider.ConfigJson"/> and the
/// secrets inside <see cref="IdentityProvider.ProviderSpecificConfigJson"/> (the Sign in with Apple private key)
/// that are still stored in plaintext. New and edited configs are protected on save
/// (<see cref="ProviderConfigSecretInterceptor"/>).
/// </summary>
public static class ProviderConfigSecretProtectionBackfill
{
    public static async Task<int> RunAsync(AuthDbContext db, ILogger logger, CancellationToken ct = default)
    {
        if (db.SecretProtectorForStorage is null)
        {
            return 0;
        }

        // A projection is not materialized as an entity, so it returns the stored (raw) value.
        var candidates = await db.IdentityProviders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => (p.ConfigJson != null && p.ConfigJson.Contains("ecret"))
                     || (p.ProviderSpecificConfigJson != null
                        && (p.ProviderSpecificConfigJson.Contains("privateKey") || p.ProviderSpecificConfigJson.Contains("PrivateKey"))))
            .Select(p => new { p.Id, p.ConfigJson, p.ProviderSpecificConfigJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ids = candidates
            .Where(c => MrWhoOidc.Auth.IdentityProviders.ProviderConfigSecrets.HasUnprotectedSecret(c.ConfigJson)
                     || MrWhoOidc.Auth.IdentityProviders.ProviderConfigSecrets.HasUnprotectedSecret(c.ProviderSpecificConfigJson))
            .Select(c => c.Id)
            .ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        var providers = await db.IdentityProviders
            .IgnoreQueryFilters()
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Forcing the columns into the UPDATE is enough: the save interceptor protects the secret members.
        foreach (var provider in providers)
        {
            db.Entry(provider).Property(p => p.ConfigJson).IsModified = true;
            db.Entry(provider).Property(p => p.ProviderSpecificConfigJson).IsModified = true;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Protected the client secret of {Count} identity provider config(s) stored in plaintext", providers.Count);
        return providers.Count;
    }
}
