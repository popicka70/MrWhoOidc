using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages;

[Authorize]
public class SwitchTenantModel(
    ITenantSwitchingService tenantSwitchingService) : PageModel
{
    public async Task<IActionResult> OnPostAsync(Guid tenantId)
    {
        // Verify user has access to this tenant
        var userTenants = await tenantSwitchingService.GetUserTenantsAsync(User);
        var targetTenant = userTenants.FirstOrDefault(t => t.TenantId == tenantId);

        if (targetTenant == null)
        {
            return Forbid();
        }

        // Switch tenant in session
        await tenantSwitchingService.SwitchTenantAsync(HttpContext, tenantId);

        // Default redirect to tenant home
        return Redirect($"/t/{targetTenant.TenantSlug}/");
    }
}
