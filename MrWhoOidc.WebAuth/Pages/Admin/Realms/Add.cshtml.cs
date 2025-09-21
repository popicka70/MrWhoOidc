using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

[Authorize]
public class AddModel(AuthDbContext db) : PageModel
{
    [BindProperty]
    public RealmInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Unique name check
        var exists = await db.Realms.AnyAsync(r => r.Name == Input.Name);
        if (exists)
        {
            ModelState.AddModelError("Input.Name", "Realm name already exists");
            return Page();
        }

        var realm = new Realm
        {
            Name = Input.Name,
            DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName
        };
        db.Realms.Add(realm);
        await db.SaveChangesAsync();
        return RedirectToPage("Edit", new { id = realm.Id });
    }

    public sealed class RealmInput
    {
        [Required, StringLength(100, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;
        [StringLength(200)]
        public string? DisplayName { get; set; }
    }
}
