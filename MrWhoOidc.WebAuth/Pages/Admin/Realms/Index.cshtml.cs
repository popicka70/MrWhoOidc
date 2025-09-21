using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

[Authorize]
public class IndexModel(AuthDbContext db) : PageModel
{
    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();

    public async Task OnGetAsync()
    {
        Realms = await db.Realms.AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == id);
        if (realm is null)
        {
            return RedirectToPage();
        }
        db.Realms.Remove(realm);
        await db.SaveChangesAsync();
        return RedirectToPage();
    }
}
