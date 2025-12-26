using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.Roles;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public sealed record RoleRow(Guid Id, string Name, Guid RealmId, string RealmName, Guid TenantId, string TenantName, bool IsActive);

    public IReadOnlyList<RoleRow> Roles { get; private set; } = Array.Empty<RoleRow>();
    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();

    [BindProperty(SupportsGet = true)]
    public Guid? RealmId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public Guid? SelectedRealmId => RealmId;

    public async Task OnGetAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            Roles = Array.Empty<RoleRow>();
            Realms = Array.Empty<Realm>();
            return;
        }

        Realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == currentTenantId.Value)
            .OrderBy(r => r.Name)
            .ToListAsync();

        // Build query scoped to current tenant
        var q = db.Roles.AsNoTracking()
            .Where(role => role.TenantId == currentTenantId.Value)
            .Join(db.Tenants, role => role.TenantId, t => t.Id, (role, t) => new { Role = role, Tenant = t })
            .Join(db.Realms, x => x.Role.RealmId, r => r.Id, (x, r) => new { x.Role, x.Tenant, Realm = r });

        if (RealmId is Guid rid)
        {
            q = q.Where(x => x.Role.RealmId == rid);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(x => x.Role.Name.Contains(s));
        }

        Roles = await q
            .OrderBy(x => x.Role.Name)
            .Select(x => new RoleRow(
                x.Role.Id,
                x.Role.Name,
                x.Role.RealmId,
                x.Realm.Name,
                x.Role.TenantId,
                x.Tenant.Name,
                x.Role.IsActive
            ))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/Roles");
        }

        var inUse = await db.UserRealmRoleAssignments.AnyAsync(a => a.RoleId == id)
            || await db.UserClientRoleAssignments.AnyAsync(a => a.RoleId == id);
        if (inUse)
        {
            TempData["Error"] = "Cannot delete a role that is assigned to a user.";
            return TenantAwareRedirect("/Admin/Roles");
        }
        var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value);
        if (entity is null) return TenantAwareRedirect("/Admin/Roles");
        db.Roles.Remove(entity);
        await db.SaveChangesAsync();
        return TenantAwareRedirect("/Admin/Roles");
    }
}
