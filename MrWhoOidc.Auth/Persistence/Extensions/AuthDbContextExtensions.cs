using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Persistence.Extensions;

public static class AuthDbContextExtensions
{
    private const string AdminRealmName = "admin";
    private const string DefaultAdminClientId = "mrwho-admin";

    /// <summary>
    /// Resolves a default client_id to drive login UX.
    /// Prefers the seeded admin client (mrwho-admin) in the admin realm; then any client in the admin realm; lastly any client.
    /// </summary>
    public static async Task<string?> ResolveDefaultClientIdAsync(this AuthDbContext db, CancellationToken ct = default)
    {
        // Prefer the seeded admin client id in the admin realm
        var preferred = await db.Clients.AsNoTracking()
            .Where(c => c.ClientId == DefaultAdminClientId)
            .Join(db.Realms.AsNoTracking(), c => c.RealmId, r => r.Id, (c, r) => new { c.ClientId, RealmName = r.Name })
            .FirstOrDefaultAsync(ct);
        if (preferred is not null && string.Equals(preferred.RealmName, AdminRealmName, StringComparison.Ordinal))
        {
            return preferred.ClientId;
        }

        // Otherwise, try any client within the admin realm
        var adminRealmId = await db.Realms.AsNoTracking()
            .Where(r => r.Name == AdminRealmName)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);
        if (adminRealmId is Guid rid)
        {
            var anyClient = await db.Clients.AsNoTracking()
                .Where(c => c.RealmId == rid)
                .OrderBy(c => c.ClientId)
                .Select(c => c.ClientId)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(anyClient)) return anyClient;
        }

        // Last resort: any client
        var fallback = await db.Clients.AsNoTracking().OrderBy(c => c.ClientId).Select(c => c.ClientId).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }
}
