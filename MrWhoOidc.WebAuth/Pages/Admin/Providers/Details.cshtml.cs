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
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : PageModel
{
    public IdentityProvider? Provider { get; private set; }
    public string ConfigPretty { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        // Check platform admin status
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        var isPlatformAdmin = platformAdminResult.Succeeded;

        // Build query with tenant filtering
        var providerQuery = db.IdentityProviders.AsNoTracking().Where(p => p.Id == id);
        
        if (!isPlatformAdmin)
        {
            // Regular tenant admins: filter by current tenant
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return NotFound(); // No tenant context
            }
            providerQuery = providerQuery.Where(p => p.TenantId == currentTenantId.Value);
        }

        Provider = await providerQuery.FirstOrDefaultAsync();
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
