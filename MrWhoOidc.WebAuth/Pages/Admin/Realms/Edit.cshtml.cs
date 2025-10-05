using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

[Authorize]
public class EditModel(AuthDbContext db) : PageModel
{
    [FromRoute]
    public Guid Id { get; set; }

    [BindProperty]
    public RealmInput Input { get; set; } = new();

    public string TenantName { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        var realmQuery = from r in db.Realms.AsNoTracking()
                         join t in db.Tenants on r.TenantId equals t.Id
                         where r.Id == Id
                         select new { Realm = r, Tenant = t };
        
        var result = await realmQuery.FirstOrDefaultAsync();
        if (result is null) return NotFound();
        
        TenantName = result.Tenant.Name;
        Input = new RealmInput { Name = result.Realm.Name, DisplayName = result.Realm.DisplayName };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) 
        {
            await LoadTenantNameAsync();
            return Page();
        }
        
        var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == Id);
        if (realm is null) return NotFound();

        // Load tenant name for display
        await LoadTenantNameAsync();

        // If name changed, validate uniqueness within tenant
        if (!string.Equals(realm.Name, Input.Name, StringComparison.Ordinal))
        {
            var exists = await db.Realms.AnyAsync(r => r.TenantId == realm.TenantId && r.Name == Input.Name);
            if (exists)
            {
                ModelState.AddModelError("Input.Name", "Realm name already exists in this tenant");
                return Page();
            }
        }

        realm.Name = Input.Name;
        realm.DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName;
        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }

    private async Task LoadTenantNameAsync()
    {
        TenantName = await db.Realms.AsNoTracking()
            .Where(r => r.Id == Id)
            .Join(db.Tenants, r => r.TenantId, t => t.Id, (r, t) => t.Name)
            .FirstOrDefaultAsync() ?? string.Empty;
    }

    public sealed class RealmInput
    {
        [Required, StringLength(100, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;
        [StringLength(200)]
        public string? DisplayName { get; set; }
    }
}
