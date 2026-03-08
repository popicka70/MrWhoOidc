using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Auth;

public class WebAuthnModel(
    ITenantAccessor tenantAccessor,
    ITenantBrandingService brandingService,
    ILogger<WebAuthnModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Username { get; set; }

    public TenantBranding? TenantBranding { get; set; }

    public async Task OnGetAsync()
    {
        logger.LogInformation("🔑 [WebAuthn Page GET] ReturnUrl: {ReturnUrl}, Username: {Username}",
            ReturnUrl ?? "(null)",
            Username ?? "(null)");

        // Load tenant branding for display
        var tenantContext = tenantAccessor.CurrentTenant;
        if (tenantContext != null)
        {
            try
            {
                TenantBranding = await brandingService.GetBrandingAsync(tenantContext.TenantId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load tenant branding for tenant {TenantId}", tenantContext.TenantId);
                TenantBranding = null;
            }
        }
    }
}
