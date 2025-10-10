using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class DeleteModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : ReadOnlyAdminPageModel
{
    public IdentityProvider? Provider { get; private set; }

    /// <summary>
    /// Validates that the current user has access to the provider based on tenant filtering.
    /// </summary>
    private async Task<bool> ValidateTenantAccessAsync(Guid providerId)
    {
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        if (platformAdminResult.Succeeded)
        {
            return true; // Platform admins can access all providers
        }

        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return false; // No tenant context
        }

        // Check if provider belongs to the current tenant
        return await db.IdentityProviders.AnyAsync(p => p.Id == providerId && p.TenantId == currentTenantId.Value);
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await ValidateTenantAccessAsync(id))
        {
            return NotFound();
        }

        Provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (Provider is null)
            return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!await ValidateTenantAccessAsync(id))
        {
            return NotFound();
        }

        var inUse = await db.ClientIdentityProviders.AnyAsync(m => m.IdentityProviderId == id);
        if (inUse)
        {
            TempData["Error"] = "Cannot delete a provider that is mapped to clients.";
            return RedirectToPage("Index");
        }

        var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id);
        if (entity is not null)
        {
            db.IdentityProviders.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage("Index");
    }
}
