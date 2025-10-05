using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class DetailsModel(AuthDbContext db) : PageModel
{
    public IdentityProvider? Provider { get; private set; }
    public string ConfigPretty { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
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
