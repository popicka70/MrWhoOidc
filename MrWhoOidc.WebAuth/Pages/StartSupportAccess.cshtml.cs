using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages;

/// <summary>
/// Endpoint for platform admins to start tenant support access.
/// </summary>
[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class StartSupportAccessModel(
    ITenantSupportAccessService supportAccessService,
    AuthDbContext db,
    IMultiTenancyOptions multiTenancyOptions) : PageModel
{
    public async Task<IActionResult> OnPostAsync(Guid tenantId, string reason, int? expiryMinutes = null, string? ticketReference = null, string? returnUrl = null)
    {
        var success = await supportAccessService.StartSupportAccessAsync(HttpContext, User, tenantId, reason, expiryMinutes, ticketReference);

        if (!success)
        {
            TempData["Error"] = "Failed to start support access. Reason is required or you may not have permission.";
            return RedirectToPage("/platform-admin/support-access");
        }

        // Redirect to tenant admin UI
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        // Get tenant slug for redirect
        var tenantSlug = await db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync();

        if (tenantSlug == null)
        {
            TempData["Error"] = "Tenant not found.";
            return RedirectToPage("/platform-admin/support-access");
        }

        // Default: redirect to tenant's admin dashboard
        // In multi-tenant mode: /t/{slug}/admin
        // In single-tenant mode: /admin
        if (multiTenancyOptions.Enabled)
        {
            return Redirect($"/t/{tenantSlug}/admin");
        }
        return RedirectToPage("/admin");
    }
}
