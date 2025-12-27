using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IRevocationService
{
    Task RevokeAsync(string token, string? tokenTypeHint, string clientId, string? ipAddress = null, CancellationToken ct = default);
    Task RevokeAllForUserAsync(Guid userId, string clientId, CancellationToken ct = default);
}

internal sealed class RevocationService(AuthDbContext db, ITenantAccessor tenantAccessor) : IRevocationService
{
    public async Task RevokeAsync(string token, string? tokenTypeHint, string clientId, string? ipAddress = null, CancellationToken ct = default)
    {
        var hash = Hash(token);
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

    static string Hash(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)));
    }
}
