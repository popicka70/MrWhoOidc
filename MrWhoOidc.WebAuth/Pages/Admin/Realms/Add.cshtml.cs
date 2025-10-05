using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

[Authorize(Policy = "tenant-admin")]
public class AddModel(AuthDbContext db) : PageModel
{
    [BindProperty]
    public RealmInput Input { get; set; } = new();

    public List<SelectListItem> TenantOptions { get; private set; } = new();

    public async Task OnGetAsync()
    {
        await LoadTenantsAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadTenantsAsync();
            return Page();
        }

        // Validate tenant exists and is active
        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == Input.TenantId && t.Status == TenantStatus.Active);
        if (!tenantExists)
        {
            ModelState.AddModelError("Input.TenantId", "Invalid tenant selected.");
            await LoadTenantsAsync();
            return Page();
        }

        // Unique name check within tenant
        var exists = await db.Realms.AnyAsync(r => r.TenantId == Input.TenantId && r.Name == Input.Name);
        if (exists)
        {
            ModelState.AddModelError("Input.Name", "Realm name already exists in this tenant");
            await LoadTenantsAsync();
            return Page();
        }

        var realm = new Realm
        {
            TenantId = Input.TenantId,
            Name = Input.Name,
            DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName
        };
        db.Realms.Add(realm);
        await db.SaveChangesAsync();
        return RedirectToPage("Edit", new { id = realm.Id });
    }

    private async Task LoadTenantsAsync()
    {
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .OrderBy(t => t.Name)
            .ToListAsync();
        TenantOptions = tenants.Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
    }

    public sealed class RealmInput
    {
        [Required]
        public Guid TenantId { get; set; }
        
        [Required, StringLength(100, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;
        
        [StringLength(200)]
        public string? DisplayName { get; set; }
    }
}
