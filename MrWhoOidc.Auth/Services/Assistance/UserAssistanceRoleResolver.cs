using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Assistance;

/// <summary>
/// Resolves the active roles of a user in one tenant and computes the effective role set of an assistance
/// session. Role ids are the stable comparison key: role names may exist in more than one realm of the same
/// tenant, and the requester and helper may hold a same-named role in different realms.
/// </summary>
public sealed class UserAssistanceRoleResolver(AuthDbContext db)
{
    /// <summary>All active role assignments of a user in a tenant, across its realms and clients.</summary>
    public async Task<Dictionary<Guid, string>> GetActiveRolesAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct = default)
    {
        var realmRoles = await db.UserRealmRoleAssignments.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(a => a.UserId == userId && a.IsActive)
            .Join(db.Roles.AsNoTracking().IgnoreQueryFilters(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(db.Realms.AsNoTracking().IgnoreQueryFilters(), x => x.r.RealmId, rl => rl.Id, (x, rl) => new { x.r, rl })
            .Where(x => x.r.IsActive && x.r.TenantId == tenantId && x.rl.TenantId == tenantId)
            .Select(x => new { x.r.Id, x.r.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var clientRoles = await db.UserClientRoleAssignments.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(a => a.UserId == userId && a.IsActive)
            .Join(db.Roles.AsNoTracking().IgnoreQueryFilters(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(db.Clients.AsNoTracking().IgnoreQueryFilters(), x => x.a.ClientId, c => c.Id, (x, c) => new { x.r, c })
            .Where(x => x.r.IsActive && x.r.TenantId == tenantId && x.c.TenantId == tenantId)
            .Select(x => new { x.r.Id, x.r.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new Dictionary<Guid, string>();
        foreach (var role in realmRoles.Concat(clientRoles))
        {
            result[role.Id] = role.Name;
        }

        return result;
    }

    public async Task<HashSet<Guid>> GetActiveRoleIdsAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
        => (await GetActiveRolesAsync(userId, tenantId, ct).ConfigureAwait(false)).Keys.ToHashSet();

    /// <summary>
    /// The role ids the helper may use for this session. Full-target mode returns the requester's roles;
    /// intersection mode returns only roles held by both the requester and the helper.
    /// </summary>
    public async Task<HashSet<Guid>> GetEffectiveRoleIdsAsync(
        UserAssistanceSession session,
        CancellationToken ct = default)
    {
        var requesterRoles = await GetActiveRoleIdsAsync(session.RequesterUserId, session.TenantId, ct)
            .ConfigureAwait(false);

        if (session.RightsMode == UserAssistanceRightsMode.FullTarget)
        {
            return requesterRoles;
        }

        var helperRoles = await GetActiveRoleIdsAsync(session.HelperUserId, session.TenantId, ct)
            .ConfigureAwait(false);

        requesterRoles.IntersectWith(helperRoles);
        return requesterRoles;
    }

    public static string SerializeRoleIds(IEnumerable<Guid> roleIds)
        => string.Join(',', roleIds.Distinct().Select(id => id.ToString("N")));

    public static HashSet<Guid> ParseRoleIds(string? serialized)
    {
        var result = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return result;
        }

        foreach (var part in serialized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var id))
            {
                result.Add(id);
            }
        }

        return result;
    }
}
