using System.Text.Json;
using LicensingService.Core.Entities;
using LicensingService.Core.Services;
using LicensingService.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensingService.Web.Pages.Licenses;

public class DetailsModel : PageModel
{
    private readonly ILicenseStore _licenseStore;
    private readonly ILicenseService _licenseService;

    public DetailsModel(ILicenseStore licenseStore, ILicenseService licenseService)
    {
        _licenseStore = licenseStore;
        _licenseService = licenseService;
    }

    public License License { get; set; } = null!;
    public IReadOnlyList<LicenseEvent> Events { get; set; } = [];
    public string? FormattedOptions { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var license = await _licenseStore.GetByIdAsync(id);
        if (license == null)
        {
            return NotFound();
        }

        License = license;
        Events = await _licenseStore.GetEventsAsync(id);

        if (!string.IsNullOrEmpty(license.Options))
        {
            try
            {
                var options = JsonSerializer.Deserialize<Dictionary<string, object>>(license.Options);
                FormattedOptions = JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                FormattedOptions = license.Options;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostRenewAsync(Guid id, DateTimeOffset validUntil)
    {
        var request = new RenewLicenseRequest
        {
            LicenseId = id,
            NewValidUntil = validUntil
        };

        var result = await _licenseService.RenewLicenseAsync(request, User.Identity?.Name ?? "admin");

        if (!result.Success)
        {
            TempData["Error"] = result.Error ?? "Failed to renew license";
            return RedirectToPage(new { id });
        }

        TempData["Success"] = "License renewed successfully.";
        return RedirectToPage(new { id = result.License!.Id });
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, string reason)
    {
        var request = new RevokeLicenseRequest
        {
            LicenseId = id,
            Reason = reason
        };

        var result = await _licenseService.RevokeLicenseAsync(request, User.Identity?.Name ?? "admin");

        if (!result.Success)
        {
            TempData["Error"] = result.Error ?? "Failed to revoke license";
            return RedirectToPage(new { id });
        }

        TempData["Success"] = "License revoked successfully.";
        return RedirectToPage(new { id });
    }
}
