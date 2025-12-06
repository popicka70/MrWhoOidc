using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages;

[Authorize]
public class SwitchTenantModel(
    ITenantSwitchingService tenantSwitchingService,
    ILogger<SwitchTenantModel> logger) : PageModel
{
    public async Task<IActionResult> OnPostAsync(Guid tenantId, string? returnUrl = null)
    {
        logger.LogInformation("🔄 [TenantSwitch] START - Requested switch to tenant {TenantId}, ReturnUrl={ReturnUrl}", tenantId, returnUrl);
        logger.LogInformation("🔄 [TenantSwitch] Current user: {UserName}, Sub={Sub}", 
            User.Identity?.Name, 
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);

        // Verify user has access to this tenant
        var userTenants = await tenantSwitchingService.GetUserTenantsAsync(User);
        logger.LogInformation("🔄 [TenantSwitch] User has access to {Count} tenants: {Tenants}", 
            userTenants.Count, 
            string.Join(", ", userTenants.Select(t => $"{t.TenantSlug}({t.TenantId})")));

        var targetTenant = userTenants.FirstOrDefault(t => t.TenantId == tenantId);

        if (targetTenant == null)
        {
            logger.LogWarning("🔄 [TenantSwitch] DENIED - User does not have access to tenant {TenantId}", tenantId);
            return Forbid();
        }

        logger.LogInformation("🔄 [TenantSwitch] Target tenant found: {TenantName} ({TenantSlug}), TenantUserId={TenantUserId}, HasAdminAccess={HasAdminAccess}", 
            targetTenant.TenantName, targetTenant.TenantSlug, targetTenant.TenantUserId, targetTenant.HasAdminAccess);

        // Switch tenant in session
        await tenantSwitchingService.SwitchTenantAsync(HttpContext, tenantId);

        var redirectUrl = $"/t/{targetTenant.TenantSlug}/";
        logger.LogInformation("🔄 [TenantSwitch] SUCCESS - Redirecting to {RedirectUrl}", redirectUrl);

        // Default redirect to tenant home
        return Redirect(redirectUrl);
    }
}
