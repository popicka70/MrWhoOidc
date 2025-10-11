using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Roles;

[Authorize(Policy = "tenant-admin")]
public class EditModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : TenantAwarePageModel(tenantAccessor)
{
    public class EditInput
    {
        [Required]
        public Guid RealmId { get; set; }
        [Required, StringLength(100)]
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
    }

    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();

    [BindProperty]
    public EditInput Input { get; set; } = new();

    public string TenantName { get; set; } = string.Empty;
    public string RealmName { get; set; } = string.Empty;

    /// <summary>
    /// Validates that the role belongs to the current tenant (defense in depth).
    /// Platform admins bypass this check.
    /// </summary>
    private async Task<bool> ValidateTenantAccessAsync(Role role)
    {
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        if (platformAdminResult.Succeeded)
        {
            return true; // Platform admins can access all roles
        }

        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return false; // No tenant context
        }

        return role.TenantId == currentTenantId.Value;
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var roleQuery = from role in db.Roles.AsNoTracking()
                        join realm in db.Realms on role.RealmId equals realm.Id
                        join tenant in db.Tenants on role.TenantId equals tenant.Id
                        where role.Id == id
                        select new { Role = role, Realm = realm, Tenant = tenant };

        var result = await roleQuery.FirstOrDefaultAsync();
        if (result is null) return TenantAwareRedirect("/Admin/Roles");

        TenantName = result.Tenant.Name;
        RealmName = result.Realm.Name;

        // Load realms filtered by tenant
        Realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == result.Role.TenantId)
            .OrderBy(r => r.Name)
            .ToListAsync();

        Input = new EditInput
        {
            RealmId = result.Role.RealmId,
            Name = result.Role.Name,
            IsActive = result.Role.IsActive
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null) return TenantAwareRedirect("/Admin/Roles");

        // Defense in depth: Validate tenant ownership
        if (!await ValidateTenantAccessAsync(entity))
        {
            return NotFound(); // Prevents cross-tenant modification
        }

        // Load tenant and realm info
        await LoadTenantAndRealmAsync(id);

        // Load realms filtered by tenant
        Realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == entity.TenantId)
            .OrderBy(r => r.Name)
            .ToListAsync();

        if (!ModelState.IsValid) return Page();

        // Validate realm belongs to same tenant
        var realmValid = await db.Realms.AnyAsync(r => r.Id == Input.RealmId && r.TenantId == entity.TenantId);
        if (!realmValid)
        {
            ModelState.AddModelError("Input.RealmId", "Realm does not belong to this tenant.");
            return Page();
        }

        if (!string.Equals(entity.Name, Input.Name, StringComparison.Ordinal))
        {
            var exists = await db.Roles.AnyAsync(r =>
                r.TenantId == entity.TenantId &&
                r.RealmId == entity.RealmId &&
                r.Name == Input.Name &&
                r.Id != id);
            if (exists)
            {
                ModelState.AddModelError("Input.Name", "Role already exists in this realm.");
                return Page();
            }
            entity.Name = Input.Name.Trim();
        }
        entity.RealmId = Input.RealmId;
        entity.IsActive = Input.IsActive;
        await db.SaveChangesAsync();
        return TenantAwareRedirect("/Admin/Roles");
    }

    private async Task LoadTenantAndRealmAsync(Guid roleId)
    {
        var result = await (from role in db.Roles.AsNoTracking()
                            join realm in db.Realms on role.RealmId equals realm.Id
                            join tenant in db.Tenants on role.TenantId equals tenant.Id
                            where role.Id == roleId
                            select new { Tenant = tenant.Name, Realm = realm.Name })
                            .FirstOrDefaultAsync();
        if (result != null)
        {
            TenantName = result.Tenant;
            RealmName = result.Realm;
        }
    }
}
