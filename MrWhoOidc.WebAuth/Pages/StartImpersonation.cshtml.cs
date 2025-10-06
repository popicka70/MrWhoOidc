using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages;

/// <summary>
/// Endpoint for platform admins to start impersonating a tenant admin.
/// </summary>
[Authorize(Policy = "platform-admin")]
public class StartImpersonationModel(
    IImpersonationService impersonationService,
    AuthDbContext db,
    IOptions<MultiTenancyOptions> multiTenancyOptions) : PageModel
{
    public async Task<IActionResult> OnPostAsync(Guid tenantId, string? returnUrl = null)
    {
        var success = await impersonationService.StartImpersonationAsync(HttpContext, User, tenantId);
        
        if (!success)
        {
            TempData["Error"] = "Failed to start impersonation. You may not have permission or the tenant may be inactive.";
            return RedirectToPage("/PlatformAdmin/Tenants/Index");
        }

        // Redirect to tenant admin UI
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        // Get tenant slug for redirect
        var tenantSlug = await db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync();

        if (tenantSlug == null)
        {
            TempData["Error"] = "Tenant not found.";
            return RedirectToPage("/PlatformAdmin/Tenants/Index");
        }

        // Default: redirect to tenant's admin dashboard
        // In multi-tenant mode: /t/{slug}/Admin/Index
        // In single-tenant mode: /Admin/Index
        if (multiTenancyOptions.Value.Enabled)
        {
            return Redirect($"/t/{tenantSlug}/Admin/Index");
        }
        return RedirectToPage("/Admin/Index");
    }
}
