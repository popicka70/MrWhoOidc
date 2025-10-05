using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Scopes;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(AuthDbContext db) : PageModel
{
    public IReadOnlyList<Scope> Scopes { get; private set; } = Array.Empty<Scope>();

    public async Task OnGetAsync()
    {
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
