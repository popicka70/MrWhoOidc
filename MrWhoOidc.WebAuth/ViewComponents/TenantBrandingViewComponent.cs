using Microsoft.AspNetCore.Mvc;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.ViewComponents;

/// <summary>
/// View component that injects tenant branding CSS variables into the page.
/// </summary>
public class TenantBrandingViewComponent : ViewComponent
{
    private readonly ITenantBrandingService _brandingService;

    public TenantBrandingViewComponent(ITenantBrandingService brandingService)
    {
        _brandingService = brandingService;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var branding = await _brandingService.GetCurrentTenantBrandingAsync();
        return View(branding);
    }
}
