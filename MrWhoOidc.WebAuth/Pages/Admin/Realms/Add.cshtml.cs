using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Extensions;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

[Authorize(Policy = "tenant-admin")]
public class AddModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : PageModel
{
    [BindProperty]
    public RealmInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var currentTenant = tenantAccessor.CurrentTenant;
        if (currentTenant is null)
        {
            ModelState.AddModelError(string.Empty, "Unable to determine current tenant context.");
            return Page();
        }

        // Unique name check within tenant
        var exists = await db.Realms.AnyAsync(r => r.TenantId == currentTenant.TenantId && r.Name == Input.Name);
        if (exists)
        {
            ModelState.AddModelError("Input.Name", "Realm name already exists in this tenant");
            return Page();
        }

        var realm = new Realm
        {
            TenantId = currentTenant.TenantId,
            Name = Input.Name,
            DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName,
            AllowUnconfirmedLogin = Input.AllowUnconfirmedLogin
        };
        db.Realms.Add(realm);
        await db.SaveChangesAsync();

        // Build tenant-aware redirect URL
        var redirectUrl = TenantAwareUrlBuilder.BuildTenantPath(
            $"/Admin/Realms/Edit/{realm.Id}",
            tenantAccessor,
            multiTenancyOptions);
        return Redirect(redirectUrl);
    }

    public sealed class RealmInput
    {
        [Required, StringLength(100, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;

        [StringLength(200)]
        public string? DisplayName { get; set; }

        [Display(Name = "Allow unconfirmed email logins")]
        public bool AllowUnconfirmedLogin { get; set; } = true;
    }
}
