using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Scopes;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    IAuthorizationService authorizationService) : PageModel
{
    public IReadOnlyList<Scope> Scopes { get; private set; } = Array.Empty<Scope>();
    public bool IsPlatformAdmin { get; private set; }

    public async Task OnGetAsync()
    {
        // Check if user is platform admin
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        IsPlatformAdmin = platformAdminResult.Succeeded;

        // Note: Scopes are global/shared across tenants in current implementation
        // Future: Consider tenant-specific scopes if needed
        Scopes = await db.Scopes.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return RedirectToPage();
        var inUse = await db.ClientScopes.AnyAsync(cs => cs.ScopeName == name);
        if (inUse)
        {
            TempData["Error"] = $"Cannot delete scope '{name}' because it is assigned to one or more clients.";
            return RedirectToPage();
        }
        var entity = await db.Scopes.FirstOrDefaultAsync(s => s.Name == name);
        if (entity is null) return RedirectToPage();
        db.Scopes.Remove(entity);
        await db.SaveChangesAsync();
        return RedirectToPage();
    }
}
