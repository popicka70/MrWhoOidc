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

        // If no scopes requested beyond openid, treat as consented
        var requested = scopes.Where(s => !string.Equals(s, "openid", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return true;

        var granted = System.Text.Json.JsonSerializer.Deserialize<string[]>(consent.ScopesJson) ?? Array.Empty<string>();
        var grantedSet = new HashSet<string>(granted, StringComparer.OrdinalIgnoreCase);
        // Ensure all requested scopes are already granted
        return requested.All(s => grantedSet.Contains(s));
    }

    public async Task GrantConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default)
    {
        var existing = await db.Consents.FirstOrDefaultAsync(c => c.UserId == userId && c.ClientId == clientId, ct);
        var requested = scopes.Where(s => !string.Equals(s, "openid", StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var scopesJson = System.Text.Json.JsonSerializer.Serialize(requested.Distinct(StringComparer.OrdinalIgnoreCase));
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
            var current = System.Text.Json.JsonSerializer.Deserialize<string[]>(existing.ScopesJson) ?? Array.Empty<string>();
            var merged = current.Concat(requested).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            existing.ScopesJson = System.Text.Json.JsonSerializer.Serialize(merged);
            existing.RevokedAt = null;
        }
        await db.SaveChangesAsync(ct);
    }
}
