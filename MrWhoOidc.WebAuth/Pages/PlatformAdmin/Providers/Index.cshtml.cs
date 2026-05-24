using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Providers;

[Authorize(Policy = "platform-admin")]
public sealed class IndexModel(AuthDbContext db) : PageModel
{
    public IReadOnlyList<IdentityProvider> Providers { get; private set; } = Array.Empty<IdentityProvider>();

    public async Task OnGetAsync()
    {
        Providers = await db.IdentityProviders.AsNoTracking()
            .Where(p => p.TenantId == null)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.DisplayName ?? p.Name)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var provider = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == null);
        if (provider is null)
        {
            return NotFound();
        }

        db.IdentityProviders.Remove(provider);
        await db.SaveChangesAsync();

        TempData["SuccessMessage"] = "Platform provider deleted.";
        return RedirectToPage();
    }
}