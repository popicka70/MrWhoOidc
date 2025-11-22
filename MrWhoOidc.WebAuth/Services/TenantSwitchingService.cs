using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Service for managing tenant switching and checking user's tenant access
/// </summary>
public interface ITenantSwitchingService
{
    /// <summary>
    /// Get all tenants the current user has access to (has roles in)
    /// </summary>
    Task<List<TenantAccessInfo>> GetUserTenantsAsync(ClaimsPrincipal user);

    /// <summary>
    /// Switch the user's current tenant context (stores in session)
    /// </summary>
    Task SwitchTenantAsync(HttpContext httpContext, Guid tenantId);

    /// <summary>
    /// Get the user's preferred tenant from session
    /// </summary>
    Guid? GetPreferredTenantId(HttpContext httpContext);
}

public class TenantSwitchingService(AuthDbContext db, ICurrentUserAccountResolver userAccountResolver) : ITenantSwitchingService
{
    private const string SessionKey = "PreferredTenantId";

    public async Task<List<TenantAccessInfo>> GetUserTenantsAsync(ClaimsPrincipal user)
    {
        var resolved = await userAccountResolver.ResolveAsync(user);
        if (resolved is null)
        {
            return new List<TenantAccessInfo>();
        }

        var normalizedEmail = resolved.Value.NormalizedEmail;

        if (string.IsNullOrEmpty(normalizedEmail) && resolved.Value.UserAccountId is Guid userAccountId)
        {
            var accountEmail = await db.UserAccounts.AsNoTracking()
                .Where(a => a.Id == userAccountId)
                .Select(a => a.NormalizedEmail ?? a.Email)
                .FirstOrDefaultAsync();
            normalizedEmail = EmailNormalizer.NormalizeForLookup(accountEmail);
        }

        if (string.IsNullOrEmpty(normalizedEmail))
        {
            var userEmail = await db.Users.AsNoTracking()
                .Where(u => u.Id == resolved.Value.UserId)
                .Select(u => u.NormalizedEmail ?? u.Email)
                .FirstOrDefaultAsync();
            normalizedEmail = EmailNormalizer.NormalizeForLookup(userEmail);
        }

        if (string.IsNullOrEmpty(normalizedEmail))
        {
            return await GetLegacyTenantsAsync(resolved.Value.UserId);
        }

        var userTenantRows = await (
            from legacyUser in db.Users.AsNoTracking()
            where legacyUser.NormalizedEmail == normalizedEmail
            join tenant in db.Tenants.AsNoTracking() on legacyUser.TenantId equals tenant.Id
            where tenant.Status == TenantStatus.Active
            select new UserTenantRow(
                legacyUser.Id,
                tenant.Id,
                tenant.Name,
                tenant.Slug,
                tenant.IssuerUri)
        ).ToListAsync();

        if (userTenantRows.Count == 0)
        {
            return await GetLegacyTenantsAsync(resolved.Value.UserId);
        }

        var userIds = userTenantRows.Select(x => x.UserId).Distinct().ToList();

        var roleAssignments = await (
            from assignment in db.UserRoleAssignments.AsNoTracking()
            where assignment.IsActive && userIds.Contains(assignment.UserId)
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            where role.IsActive
            select new { assignment.UserId, role.Name }
        ).ToListAsync();

        var rolesByUser = roleAssignments.ToLookup(x => x.UserId, x => x.Name);

        var adminTenantIds = resolved.Value.UserAccountId is Guid membershipAccountId
            ? (await db.UserTenantMemberships.AsNoTracking()
                .Where(m => m.UserAccountId == membershipAccountId
                            && m.Status == TenantMembershipStatus.Active
                            && m.IsTenantAdmin)
                .Select(m => m.TenantId)
                .ToListAsync())
                .ToHashSet()
            : new HashSet<Guid>();

        var tenants = userTenantRows
            .GroupBy(x => new { x.TenantId, x.TenantName, x.TenantSlug, x.IssuerUri })
            .Select(g =>
            {
                var roleNames = g.SelectMany(entry => rolesByUser[entry.UserId])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var hasAdminRole = roleNames.Any(role =>
                    string.Equals(role, "platform-admin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, "tenant-admin", StringComparison.OrdinalIgnoreCase));

                return new TenantAccessInfo
                {
                    TenantId = g.Key.TenantId,
                    TenantName = g.Key.TenantName,
                    TenantSlug = g.Key.TenantSlug,
                    IssuerUri = g.Key.IssuerUri,
                    HasAdminAccess = hasAdminRole || adminTenantIds.Contains(g.Key.TenantId),
                    RoleCount = roleNames.Count
                };
            })
            .OrderBy(t => t.TenantName)
            .ToList();

        return tenants;
    }

    public Task SwitchTenantAsync(HttpContext httpContext, Guid tenantId)
    {
        httpContext.Session.SetString(SessionKey, tenantId.ToString());
        return Task.CompletedTask;
    }

    public Guid? GetPreferredTenantId(HttpContext httpContext)
    {
        var tenantIdStr = httpContext.Session.GetString(SessionKey);
        if (Guid.TryParse(tenantIdStr, out var tenantId))
        {
            return tenantId;
        }
        return null;
    }

    private async Task<List<TenantAccessInfo>> GetLegacyTenantsAsync(Guid userId)
    {
        var tenantAccess = await db.UserRoleAssignments
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.IsActive)
            .Join(db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(db.Realms, ar => ar.r.RealmId, rl => rl.Id, (ar, rl) => new { ar.a, ar.r, rl })
            .Join(db.Tenants, x => x.rl.TenantId, t => t.Id, (x, t) => new { x.a, x.r, x.rl, t })
            .Where(x => x.t.Status == TenantStatus.Active)
            .Select(x => new
            {
                TenantId = x.t.Id,
                TenantName = x.t.Name,
                TenantSlug = x.t.Slug,
                IssuerUri = x.t.IssuerUri,
                RoleName = x.r.Name
            })
            .ToListAsync();

        return tenantAccess
            .GroupBy(x => new { x.TenantId, x.TenantName, x.TenantSlug, x.IssuerUri })
            .Select(g => new TenantAccessInfo
            {
                TenantId = g.Key.TenantId,
                TenantName = g.Key.TenantName,
                TenantSlug = g.Key.TenantSlug,
                IssuerUri = g.Key.IssuerUri,
                HasAdminAccess = g.Any(x => x.RoleName == "platform-admin" || x.RoleName == "tenant-admin"),
                RoleCount = g.Select(x => x.RoleName).Distinct().Count()
            })
            .OrderBy(t => t.TenantName)
            .ToList();
    }

    private sealed record UserTenantRow(Guid UserId, Guid TenantId, string TenantName, string TenantSlug, string IssuerUri);
}

public class TenantAccessInfo
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string TenantSlug { get; set; } = string.Empty;
    public string IssuerUri { get; set; } = string.Empty;
    public bool HasAdminAccess { get; set; }
    public int RoleCount { get; set; }
}
