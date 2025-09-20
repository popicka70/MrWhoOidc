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

    [BindProperty]
    public RealmInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        Realms = await db.Realms.AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await OnGetAsync();
            return Page();
        }

        // Ensure unique name
        var exists = await db.Realms.AnyAsync(r => r.Name == Input.Name);
        if (exists)
        {
            ModelState.AddModelError("Input.Name", "Realm name already exists");
            await OnGetAsync();
            return Page();
        }

        var realm = new Realm
        {
            Name = Input.Name,
            DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName
        };
        db.Realms.Add(realm);
        await db.SaveChangesAsync();
        return RedirectToPage();
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

    public sealed class RealmInput
    {
        [Required, StringLength(100, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;
        [StringLength(200)]
        public string? DisplayName { get; set; }
    }
}
