using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Roles;

[Authorize]
public class AddModel(AuthDbContext db) : PageModel
{
    public class AddInput
    {
        [Required]
        public Guid TenantId { get; set; }
        
        [Required]
        public Guid RealmId { get; set; }
        
        [Required, StringLength(100)]
        public string Name { get; set; } = string.Empty;
        
        public bool IsActive { get; set; } = true;
    }

    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();
    public List<SelectListItem> TenantOptions { get; private set; } = new();

    [BindProperty]
    public AddInput Input { get; set; } = new();

    public async Task OnGetAsync(Guid? tenantId = null)
    {
        await LoadDataAsync(tenantId);
        if (tenantId.HasValue)
        {
            Input.TenantId = tenantId.Value;
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadDataAsync(Input.TenantId);
            return Page();
        }

        // Validate tenant exists and is active
        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == Input.TenantId && t.Status == TenantStatus.Active);
        if (!tenantExists)
        {
            ModelState.AddModelError("Input.TenantId", "Invalid tenant selected.");
            await LoadDataAsync(Input.TenantId);
            return Page();
        }

        // Validate realm belongs to tenant
        var realmValid = await db.Realms.AnyAsync(r => r.Id == Input.RealmId && r.TenantId == Input.TenantId);
        if (!realmValid)
        {
            ModelState.AddModelError("Input.RealmId", "Realm does not belong to the selected tenant.");
            await LoadDataAsync(Input.TenantId);
            return Page();
        }

        Input.Name = Input.Name.Trim();
        var exists = await db.Roles.AnyAsync(r => r.TenantId == Input.TenantId && r.RealmId == Input.RealmId && r.Name == Input.Name);
        if (exists)
        {
            ModelState.AddModelError("Input.Name", "Role already exists in this realm.");
            await LoadDataAsync(Input.TenantId);
            return Page();
        }

        db.Roles.Add(new Role 
        { 
            TenantId = Input.TenantId,
            RealmId = Input.RealmId, 
            Name = Input.Name, 
            IsActive = Input.IsActive 
        });
        await db.SaveChangesAsync();
        return RedirectToPage("Index", new { TenantId = Input.TenantId });
    }

    private async Task LoadDataAsync(Guid? tenantId)
    {
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .OrderBy(t => t.Name)
            .ToListAsync();
        TenantOptions = tenants.Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();

        // Load realms filtered by tenant if provided
        var realmQuery = db.Realms.AsNoTracking().AsQueryable();
        if (tenantId.HasValue)
        {
            realmQuery = realmQuery.Where(r => r.TenantId == tenantId.Value);
        }
        Realms = await realmQuery.OrderBy(r => r.Name).ToListAsync();
    }
}
