using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Roles;

[Authorize]
public class IndexModel(AuthDbContext db) : PageModel
{
    public sealed record RoleRow(Guid Id, string Name, Guid RealmId, string RealmName, Guid TenantId, string TenantName, bool IsActive);

    public IReadOnlyList<RoleRow> Roles { get; private set; } = Array.Empty<RoleRow>();
    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();
    public List<SelectListItem> TenantOptions { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public Guid? RealmId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? TenantId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public Guid? SelectedRealmId => RealmId;

    public async Task OnGetAsync()
    {
        // Load tenants for filter
        var allTenants = await db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .OrderBy(t => t.Name)
            .ToListAsync();
        TenantOptions = allTenants.Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
        TenantOptions.Insert(0, new SelectListItem("All Tenants", ""));

        // Filter realms by tenant if selected
        var realmQuery = db.Realms.AsNoTracking().AsQueryable();
        if (TenantId.HasValue)
        {
            realmQuery = realmQuery.Where(r => r.TenantId == TenantId.Value);
        }
        Realms = await realmQuery.OrderBy(r => r.Name).ToListAsync();

        // Build query with tenant and realm JOINs
        var q = db.Roles.AsNoTracking()
            .Join(db.Tenants, role => role.TenantId, t => t.Id, (role, t) => new { Role = role, Tenant = t })
            .Join(db.Realms, x => x.Role.RealmId, r => r.Id, (x, r) => new { x.Role, x.Tenant, Realm = r });

        if (TenantId.HasValue)
        {
            q = q.Where(x => x.Role.TenantId == TenantId.Value);
        }

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
        var inUse = await db.UserRoleAssignments.AnyAsync(a => a.RoleId == id);
        if (inUse)
        {
            TempData["Error"] = "Cannot delete a role that is assigned to a user.";
            return RedirectToPage();
        }
        var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null) return RedirectToPage();
        db.Roles.Remove(entity);
        await db.SaveChangesAsync();
        return RedirectToPage(new { TenantId, RealmId });
    }
}
