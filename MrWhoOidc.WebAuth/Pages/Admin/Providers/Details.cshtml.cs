using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class DetailsModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor) : PageModel
{
    public IdentityProvider? Provider { get; private set; }
    public string ConfigPretty { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        Provider = await db.IdentityProviders.AsNoTracking()
            .Where(p => p.Id == id && p.TenantId == currentTenantId.Value)
            .FirstOrDefaultAsync();
        if (Provider is null)
            return NotFound();

        if (!string.IsNullOrWhiteSpace(Provider.ConfigJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(Provider.ConfigJson);
                ConfigPretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                ConfigPretty = Provider.ConfigJson!; // show raw if invalid JSON
            }
        }
        return Page();
    }
}
