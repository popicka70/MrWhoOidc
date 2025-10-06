using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages;

/// <summary>
/// Endpoint for platform admins to start impersonating a tenant admin.
/// </summary>
[Authorize(Policy = "platform-admin")]
public class StartImpersonationModel(IImpersonationService impersonationService) : PageModel
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

        // Default: redirect to tenant's admin dashboard
        return RedirectToPage("/Admin/Index");
    }
}
