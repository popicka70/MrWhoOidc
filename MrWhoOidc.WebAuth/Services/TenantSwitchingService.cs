using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
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

public class TenantSwitchingService(AuthDbContext db) : ITenantSwitchingService
{
    private const string SessionKey = "PreferredTenantId";

    public async Task<List<TenantAccessInfo>> GetUserTenantsAsync(ClaimsPrincipal user)
    {
        var sub = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId))
        {
            return new List<TenantAccessInfo>();
        }

        // Get all tenants where user has any role assignment
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

        // Group by tenant and check for platform-admin or tenant-admin roles
        var tenants = tenantAccess
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
