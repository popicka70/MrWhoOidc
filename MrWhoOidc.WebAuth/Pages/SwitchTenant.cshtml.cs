using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages;

[Authorize]
public class SwitchTenantModel(
    ITenantSwitchingService tenantSwitchingService,
    IOptions<MultiTenancyOptions> multiTenancyOptions) : PageModel
{
    public async Task<IActionResult> OnPostAsync(Guid tenantId, string? returnUrl = null)
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

        var redirectUrl = BuildRedirectUrl(returnUrl, targetTenant, multiTenancyOptions.Value);
        if (redirectUrl != null)
        {
            return Redirect(redirectUrl);
        }

        // Default redirect to tenant home
        return Redirect($"/t/{targetTenant.TenantSlug}/");
    }

    private string? BuildRedirectUrl(string? returnUrl, TenantAccessInfo targetTenant, MultiTenancyOptions options)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl))
        {
            return null;
        }

        if (!options.Enabled)
        {
            return returnUrl;
        }

        var normalized = NormalizeReturnUrl(returnUrl!);
        if (!normalized.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
        {
            return $"/t/{targetTenant.TenantSlug}{normalized}";
        }

        var secondSlashIndex = normalized.IndexOf('/', startIndex: 3);
        if (secondSlashIndex == -1)
        {
            return $"/t/{targetTenant.TenantSlug}/";
        }

        var remainder = normalized[secondSlashIndex..];
        return $"/t/{targetTenant.TenantSlug}{remainder}";
    }

    private static string NormalizeReturnUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "/";
        }

        if (url.StartsWith('/'))
        {
            return url;
        }

        return "/" + url;
    }
}
