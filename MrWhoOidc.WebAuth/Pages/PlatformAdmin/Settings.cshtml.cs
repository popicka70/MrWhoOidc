using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin;

[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class SettingsModel : PageModel
{
    private readonly IPlatformSettingsService _settingsService;

    public SettingsModel(IPlatformSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [BindProperty]
    public bool QrLoginAtDiscoveryEnabled { get; set; }

    public async Task OnGetAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        QrLoginAtDiscoveryEnabled = settings.QrLoginAtDiscoveryEnabled;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.QrLoginAtDiscoveryEnabled = QrLoginAtDiscoveryEnabled;
        await _settingsService.UpdateSettingsAsync(settings, User.Identity?.Name);
        
        TempData["SuccessMessage"] = "Platform settings saved successfully.";
        return RedirectToPage();
    }
}
