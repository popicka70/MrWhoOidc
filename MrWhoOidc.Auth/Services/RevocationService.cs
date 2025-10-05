using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IRevocationService
{
    Task RevokeAsync(string token, string? tokenTypeHint, string clientId, string? ipAddress = null, CancellationToken ct = default);
}

internal sealed class RevocationService(AuthDbContext db, ITenantAccessor tenantAccessor) : IRevocationService
{
    public async Task RevokeAsync(string token, string? tokenTypeHint, string clientId, string? ipAddress = null, CancellationToken ct = default)
    {
        var hash = Hash(token);
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        // Idempotency: if already revoked or audit exists, return OK
        var already = await db.Tokens.AsNoTracking().AnyAsync(t => t.TokenHash == hash && t.Type == "refresh" && t.RevokedAt != null && t.TenantId == tenantId, ct).ConfigureAwait(false);
        if (!already)
        {
            var rt = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Type == "refresh" && t.TenantId == tenantId, ct).ConfigureAwait(false);
            if (rt is not null && string.Equals(rt.ClientId, clientId, StringComparison.Ordinal))
            {
                rt.RevokedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
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

    static string Hash(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)));
    }
}
