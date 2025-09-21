using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Roles;

[Authorize]
public class IndexModel(AuthDbContext db) : PageModel
{
    public IReadOnlyList<Role> Roles { get; private set; } = Array.Empty<Role>();
    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();

    [BindProperty(SupportsGet = true)]
    public Guid? RealmId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public Guid? SelectedRealmId => RealmId;

    public string RealmNameById(Guid id) => Realms.FirstOrDefault(r => r.Id == id)?.Name ?? id.ToString();

    public async Task OnGetAsync()
    {
        Realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        var q = db.Roles.AsNoTracking().AsQueryable();
        if (RealmId is Guid rid) q = q.Where(r => r.RealmId == rid);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(r => r.Name.Contains(s));
        }
        Roles = await q.OrderBy(r => r.Name).ToListAsync();
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
        return RedirectToPage();
    }
}
