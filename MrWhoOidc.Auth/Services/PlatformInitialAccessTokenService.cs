using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public sealed class PlatformInitialAccessTokenService(AuthDbContext db, HybridCache cache) : IPlatformInitialAccessTokenService
{
    private const string ActiveHashesCacheKey = "platform:initial-access-tokens:active-hashes";

    public async Task<IReadOnlyList<PlatformInitialAccessToken>> GetActiveAsync(CancellationToken ct = default)
    {
        return await db.PlatformInitialAccessTokens
            .AsNoTracking()
            .Where(t => t.RevokedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(PlatformInitialAccessToken Entity, string PlaintextToken)> CreateAsync(string? description, string? createdBy, CancellationToken ct = default)
    {
        var token = GenerateToken();
        var hash = HashToken(token);

        var entity = new PlatformInitialAccessToken
        {
            Id = GuidHelper.NewId(),
            TokenHash = hash,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            RevokedAt = null,
            RevokedBy = null
        };

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async innerCt =>
        {
            db.PlatformInitialAccessTokens.Add(entity);
            await db.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await cache.RemoveAsync(ActiveHashesCacheKey, ct).ConfigureAwait(false);
        return (entity, token);
    }

    public async Task<bool> RevokeAsync(Guid id, string? revokedBy, CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var success = false;

        await strategy.ExecuteAsync(async innerCt =>
        {
            var token = await db.PlatformInitialAccessTokens.FirstOrDefaultAsync(t => t.Id == id, innerCt).ConfigureAwait(false);
            if (token == null || token.RevokedAt != null)
            {
                success = false;
                return;
            }

            token.RevokedAt = DateTimeOffset.UtcNow;
            token.RevokedBy = revokedBy;
            await db.SaveChangesAsync(innerCt).ConfigureAwait(false);
            success = true;
        }, ct).ConfigureAwait(false);

        if (success)
        {
            await cache.RemoveAsync(ActiveHashesCacheKey, ct).ConfigureAwait(false);
        }

        return success;
    }

    public async Task<bool> ValidateAsync(string plaintextToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken)) return false;

        var tokenHash = HashToken(plaintextToken.Trim());
        var hashes = await GetActiveHashesAsync(ct).ConfigureAwait(false);
        return hashes.Contains(tokenHash, StringComparer.Ordinal);
    }

    private async Task<HashSet<string>> GetActiveHashesAsync(CancellationToken ct)
    {
        var opts = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(15),
            LocalCacheExpiration = TimeSpan.FromMinutes(5)
        };

        return await cache.GetOrCreateAsync(
            ActiveHashesCacheKey,
            async cancel =>
            {
                var hashes = await db.PlatformInitialAccessTokens
                    .AsNoTracking()
                    .Where(t => t.RevokedAt == null)
                    .Select(t => t.TokenHash)
                    .ToListAsync(cancel)
                    .ConfigureAwait(false);

                return hashes.ToHashSet(StringComparer.Ordinal);
            },
            opts,
            tags: new[] { "platform-settings", "initial-access-tokens" },
            cancellationToken: ct
        ).ConfigureAwait(false);
    }

    private static string GenerateToken()
    {
        // Prefix helps operators identify the token type.
        // Use base64url without padding for easy copy/paste.
        var bytes = RandomNumberGenerator.GetBytes(48);
        return "iat_" + Base64UrlEncode(bytes);
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
