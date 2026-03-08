using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.Roles;

[Authorize(Policy = "tenant-admin")]
public class AddModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public class AddInput
    {
        [Required]
        public Guid RealmId { get; set; }

        [Required, StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
    }

    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();

    [BindProperty]
    public AddInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadRealmsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            ModelState.AddModelError(string.Empty, "Tenant context is required to add a role.");
            await LoadRealmsAsync();
            return Page();
        }

        if (!ModelState.IsValid)
        {
            await LoadRealmsAsync();
            return Page();
        }

        // Validate realm belongs to tenant
        var realmValid = await db.Realms.AnyAsync(r => r.Id == Input.RealmId && r.TenantId == currentTenantId.Value);
        if (!realmValid)
        {
            ModelState.AddModelError("Input.RealmId", "Realm does not belong to the current tenant.");
            await LoadRealmsAsync();
            return Page();
        }

        Input.Name = Input.Name.Trim();
        var exists = await db.Roles.AnyAsync(r => r.TenantId == currentTenantId.Value && r.RealmId == Input.RealmId && r.Name == Input.Name);
        if (exists)
        {
            ModelState.AddModelError("Input.Name", "Role already exists in this realm.");
            await LoadRealmsAsync();
            return Page();
        }

        db.Roles.Add(new Role
        {
            TenantId = currentTenantId.Value,
            RealmId = Input.RealmId,
            Name = Input.Name,
            IsActive = Input.IsActive
        });
        await db.SaveChangesAsync();
        return TenantAwareRedirect("/Admin/Roles");
    }

    private async Task LoadRealmsAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            Realms = Array.Empty<Realm>();
            return;
        }

        Realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == currentTenantId.Value)
            .OrderBy(r => r.Name)
            .ToListAsync();
    }
}
