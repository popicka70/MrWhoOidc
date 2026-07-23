using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages;

/// <summary>
/// Endpoint to stop support access and return to platform admin view.
/// </summary>
[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class StopSupportAccessModel(ITenantSupportAccessService supportAccessService) : PageModel
{
    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        await supportAccessService.StopSupportAccessAsync(HttpContext);

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        // Default: redirect to platform admin dashboard
        return Redirect("/platform-admin");
    }
}
