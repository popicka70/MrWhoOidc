using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.Roles;

/// <summary>
/// Confirmation page for deleting a tenant role.
/// Seeded system roles (admin, licensing-admin, tenant-admin, platform-admin) are never deletable: the
/// authorization policies match on those names, so removing one locks administrators out of the tenant.
/// Roles still assigned to users (directly or per client) are refused as well. Both guards run on POST,
/// not only in the UI, so crafted requests are refused too.
/// </summary>
[Authorize(Policy = "tenant-admin")]
public class DeleteModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public string? RoleName { get; private set; }
    public string? RealmName { get; private set; }
    public bool Found { get; private set; }
    public bool IsSystemRole { get; private set; }
    public int AssignmentCount { get; private set; }

    private async Task LoadAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return;
        }

        var row = await db.Roles.AsNoTracking()
            .Where(r => r.Id == id && r.TenantId == currentTenantId.Value)
            .Join(db.Realms, r => r.RealmId, realm => realm.Id, (r, realm) => new { Role = r, Realm = realm })
            .FirstOrDefaultAsync();
        if (row is null)
        {
            return;
        }

        RoleName = row.Role.Name;
        RealmName = row.Realm.Name;
        IsSystemRole = SystemRoles.IsProtected(row.Role.Name);
        AssignmentCount = await db.UserRealmRoleAssignments.AsNoTracking().CountAsync(a => a.RoleId == id)
            + await db.UserClientRoleAssignments.AsNoTracking().CountAsync(a => a.RoleId == id);
        Found = true;
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        await LoadAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        await LoadAsync(id);
        if (!Found)
        {
            return NotFound();
        }

        if (IsSystemRole)
        {
            TempData["Error"] = $"The role '{RoleName}' is a system role and cannot be deleted.";
            return TenantAwareRedirect("/Admin/Roles");
        }

        if (AssignmentCount > 0)
        {
            TempData["Error"] = $"The role '{RoleName}' is still assigned to {AssignmentCount} user(s). Remove the assignments first.";
            return TenantAwareRedirect("/Admin/Roles");
        }

        var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == id);
        if (entity is not null)
        {
            db.Roles.Remove(entity);
            await db.SaveChangesAsync();
        }

        return TenantAwareRedirect("/Admin/Roles");
    }
}
