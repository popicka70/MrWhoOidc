using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Roles;

[Authorize]
public class AddModel(AuthDbContext db) : PageModel
{
    public class AddInput
    {
        [Required]
        public Guid RealmId { get; set; }
        [Required, StringLength(100)]
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
    }

    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();

    [BindProperty]
    public AddInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        Realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        if (!ModelState.IsValid) return Page();
        Input.Name = Input.Name.Trim();
        var exists = await db.Roles.AnyAsync(r => r.RealmId == Input.RealmId && r.Name == Input.Name);
        if (exists)
        {
            ModelState.AddModelError("Input.Name", "Role already exists in this realm.");
            return Page();
        }
        db.Roles.Add(new Role { RealmId = Input.RealmId, Name = Input.Name, IsActive = Input.IsActive });
        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }
}
