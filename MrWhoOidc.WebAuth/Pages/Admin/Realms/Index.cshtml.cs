using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

[Authorize]
public class IndexModel(AuthDbContext db) : PageModel
{
    public sealed record RealmRow(Guid Id, string Name, string? DisplayName, DateTimeOffset CreatedAt, Guid TenantId, string TenantName);

    public IReadOnlyList<RealmRow> Realms { get; private set; } = Array.Empty<RealmRow>();
    public List<SelectListItem> TenantOptions { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public Guid? TenantId { get; set; }

    public async Task OnGetAsync()
    {
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .OrderBy(t => t.Name)
            .ToListAsync();
        TenantOptions = tenants.Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
        TenantOptions.Insert(0, new SelectListItem("All Tenants", ""));

        var q = db.Realms.AsNoTracking()
            .Join(db.Tenants, r => r.TenantId, t => t.Id, (r, t) => new { Realm = r, Tenant = t });

        if (TenantId.HasValue)
        {
            q = q.Where(x => x.Realm.TenantId == TenantId.Value);
        }

        Realms = await q
            .OrderBy(x => x.Realm.Name)
            .Select(x => new RealmRow(
                x.Realm.Id,
                x.Realm.Name,
                x.Realm.DisplayName,
                x.Realm.CreatedAt,
                x.Realm.TenantId,
                x.Tenant.Name
            ))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == id);
        if (realm is null)
        {
            return RedirectToPage();
        }
        db.Realms.Remove(realm);
        await db.SaveChangesAsync();
        return RedirectToPage(new { TenantId });
    }
}
