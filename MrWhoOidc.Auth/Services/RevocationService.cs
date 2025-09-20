using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IRevocationService
{
    Task RevokeAsync(string token, string? tokenTypeHint, string clientId, CancellationToken ct = default);
}

internal sealed class RevocationService(AuthDbContext db) : IRevocationService
{
    public async Task RevokeAsync(string token, string? tokenTypeHint, string clientId, CancellationToken ct = default)
    {
        // Only refresh tokens are revocable centrally (JWT access tokens are self-contained)
        var hash = Hash(token);
        var rt = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Type == "refresh", ct);
        if (rt is not null && string.Equals(rt.ClientId, clientId, StringComparison.Ordinal))
        {
            rt.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        // RFC 7009 requires 200 OK even if token not found
    }

    static string Hash(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)));
    }
}
