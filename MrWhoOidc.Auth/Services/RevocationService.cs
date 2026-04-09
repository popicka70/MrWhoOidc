using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for revoking tokens (RFC 7009).
/// </summary>
public interface IRevocationService
{
    /// <summary>
    /// Revokes a specific token (refresh or opaque access token).
    /// Implements RFC 7009.
    /// </summary>
    /// <param name="token">The token to revoke.</param>
    /// <param name="tokenTypeHint">Optional hint about the token type.</param>
    /// <param name="clientId">The client ID requesting revocation.</param>
    /// <param name="ipAddress">Optional IP address of the requester.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RevokeAsync(string token, string? tokenTypeHint, string clientId, string? ipAddress = null, CancellationToken ct = default);

    /// <summary>
    /// Revokes all tokens (refresh and access) for a specific user and client.
    /// Used for security remediation when token reuse is detected.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RevokeAllForUserAsync(Guid userId, string clientId, CancellationToken ct = default);

    /// <summary>
    /// Revokes all refresh tokens in the same rotation family as the specified token.
    /// </summary>
    /// <param name="tokenId">Refresh token ID within the family.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RevokeRefreshTokenFamilyAsync(Guid tokenId, CancellationToken ct = default);
}

internal sealed class RevocationService(AuthDbContext db, ITenantAccessor tenantAccessor) : IRevocationService
{
    public async Task RevokeAsync(string token, string? tokenTypeHint, string clientId, string? ipAddress = null, CancellationToken ct = default)
    {
        var hash = CryptoHelper.ComputeSha256Base64(token);
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        // Support both refresh and access (opaque) tokens. 
        // If hint is provided, we use it to narrow down, otherwise we check both.
        var query = db.Tokens.Where(t => t.TokenHash == hash && t.TenantId == tenantId && t.ClientId == clientId);

        if (tokenTypeHint == "refresh_token") query = query.Where(t => t.Type == "refresh");
        else if (tokenTypeHint == "access_token") query = query.Where(t => t.Type == "access");

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var entities = await query.ToListAsync(ct).ConfigureAwait(false);
            bool changed = false;
            foreach (var entity in entities)
            {
                if (entity.RevokedAt == null)
                {
                    entity.RevokedAt = DateTimeOffset.UtcNow;
                    changed = true;
                }
            }

            if (changed)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        else
        {
            // ⚡ Bolt Optimization: Use ExecuteUpdateAsync for bulk update instead of loading entities into memory
            await query
                .Where(t => t.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(b => b.RevokedAt, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);
        }

        // Audit (best effort)
        db.RevocationAudits.Add(new RevocationAudit
        {
            ClientId = clientId,
            TokenHash = hash,
            TokenType = tokenTypeHint,
            IpAddress = ipAddress,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RevokeAllForUserAsync(Guid userId, string clientId, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        var query = db.Tokens
            .Where(t => t.UserId == userId && t.ClientId == clientId && t.TenantId == tenantId && t.RevokedAt == null);

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var tokens = await query.ToListAsync(ct).ConfigureAwait(false);

            if (tokens.Count > 0)
            {
                foreach (var t in tokens) t.RevokedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        else
        {
            // ⚡ Bolt Optimization: Use ExecuteUpdateAsync for bulk update instead of loading entities into memory
            await query.ExecuteUpdateAsync(
                setters => setters.SetProperty(b => b.RevokedAt, DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }
    }

    public async Task RevokeRefreshTokenFamilyAsync(Guid tokenId, CancellationToken ct = default)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        var current = await db.Tokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.TenantId == tenantId && t.Type == "refresh", ct)
            .ConfigureAwait(false);
        if (current is null)
        {
            return;
        }

        var lineagePool = await db.Tokens
            .Where(t => t.TenantId == tenantId && t.Type == "refresh" && t.UserId == current.UserId && t.ClientId == current.ClientId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (lineagePool.Count == 0)
        {
            return;
        }

        var byId = lineagePool.ToDictionary(t => t.Id);

        // ReplacedById stores the previous token ID (parent) for rotation lineage.
        var root = current;
        var visitedAncestors = new HashSet<Guid>();
        while (root.ReplacedById is Guid parentId && visitedAncestors.Add(root.Id) && byId.TryGetValue(parentId, out var parent))
        {
            root = parent;
        }

        var familyIds = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(root.Id);

        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (!familyIds.Add(next))
            {
                continue;
            }

            foreach (var child in lineagePool.Where(t => t.ReplacedById == next))
            {
                queue.Enqueue(child.Id);
            }
        }

        if (familyIds.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var changed = false;
        foreach (var token in lineagePool.Where(t => familyIds.Contains(t.Id) && t.RevokedAt == null))
        {
            token.RevokedAt = now;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
