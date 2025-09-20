using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IRevocationService
{
    Task RevokeAsync(string token, string? tokenTypeHint, string clientId, string? ipAddress = null, CancellationToken ct = default);
}

internal sealed class RevocationService(AuthDbContext db) : IRevocationService
{
    public async Task RevokeAsync(string token, string? tokenTypeHint, string clientId, string? ipAddress = null, CancellationToken ct = default)
    {
        var hash = Hash(token);

        // Idempotency: if already revoked or audit exists, return OK
        var already = await db.Tokens.AsNoTracking().AnyAsync(t => t.TokenHash == hash && t.Type == "refresh" && t.RevokedAt != null, ct);
        if (!already)
        {
            var rt = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Type == "refresh", ct);
            if (rt is not null && string.Equals(rt.ClientId, clientId, StringComparison.Ordinal))
            {
                rt.RevokedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
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
        await db.SaveChangesAsync(ct);
    }

    static string Hash(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)));
    }
}
