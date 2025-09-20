using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

[Authorize]
public class EditModel(AuthDbContext db) : PageModel
{
    [FromRoute]
    public Guid Id { get; set; }

    [BindProperty]
    public RealmInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var realm = await db.Realms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == Id);
        if (realm is null) return NotFound();
        Input = new RealmInput { Name = realm.Name, DisplayName = realm.DisplayName };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == Id);
        if (realm is null) return NotFound();

        // If name changed, validate uniqueness
        if (!string.Equals(realm.Name, Input.Name, StringComparison.Ordinal))
        {
            var exists = await db.Realms.AnyAsync(r => r.Name == Input.Name);
            if (exists)
            {
                ModelState.AddModelError("Input.Name", "Realm name already exists");
                return Page();
            }
        }

        realm.Name = Input.Name;
        realm.DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName;
        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }

    public sealed class RealmInput
    {
        [Required, StringLength(100, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;
        [StringLength(200)]
        public string? DisplayName { get; set; }
    }
}
