using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IConsentService
{
    Task<bool> HasConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default);
    Task GrantConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default);
}

internal sealed class ConsentService(AuthDbContext db) : IConsentService
{
    public async Task<bool> HasConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default)
    {
        var consent = await db.Consents.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId && c.ClientId == clientId && c.RevokedAt == null, ct);
        if (consent is null) return false;
        // Basic: ignore per-scope checks for now, any consent covers scopes requested
        return true;
    }

    public async Task GrantConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default)
    {
        var existing = await db.Consents.FirstOrDefaultAsync(c => c.UserId == userId && c.ClientId == clientId, ct);
        var scopesJson = System.Text.Json.JsonSerializer.Serialize(scopes);
        if (existing is null)
        {
            db.Consents.Add(new Consent
            {
                UserId = userId,
                ClientId = clientId,
                ScopesJson = scopesJson,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.ScopesJson = scopesJson;
            existing.RevokedAt = null;
        }
        await db.SaveChangesAsync(ct);
    }
}
