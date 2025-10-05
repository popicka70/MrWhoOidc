using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class DeleteModel(AuthDbContext db) : PageModel
{
    public IdentityProvider? Provider { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (Provider is null)
            return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var inUse = await db.ClientIdentityProviders.AnyAsync(m => m.IdentityProviderId == id);
        if (inUse)
        {
            TempData["Error"] = "Cannot delete a provider that is mapped to clients.";
            return RedirectToPage("Index");
        }

        var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id);
        if (entity is not null)
        {
            db.IdentityProviders.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage("Index");
    }
}
