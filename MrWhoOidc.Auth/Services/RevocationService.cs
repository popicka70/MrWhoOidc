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
        
        var tokens = await db.Tokens
            .Where(t => t.UserId == userId && t.ClientId == clientId && t.TenantId == tenantId && t.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        if (tokens.Count > 0)
        {
            foreach (var t in tokens) t.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
