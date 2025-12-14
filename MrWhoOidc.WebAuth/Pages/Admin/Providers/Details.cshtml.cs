using System.Text.Json;
using System.Text.Json.Nodes;
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
                var node = JsonNode.Parse(Provider.ConfigJson);
                if (node is JsonObject obj)
                {
                    // Avoid leaking secrets in the UI.
                    var secretKey = obj.Select(kvp => kvp.Key)
                        .FirstOrDefault(k => string.Equals(k, "ClientSecret", StringComparison.OrdinalIgnoreCase));
                    if (secretKey is not null)
                    {
                        obj[secretKey] = "<redacted>";
                    }

                    ConfigPretty = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                }
                else
                {
                    using var doc = JsonDocument.Parse(Provider.ConfigJson);
                    ConfigPretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                }
            }
            catch
            {
                ConfigPretty = Provider.ConfigJson!; // show raw if invalid JSON
            }
        }
        return Page();
    }
}
