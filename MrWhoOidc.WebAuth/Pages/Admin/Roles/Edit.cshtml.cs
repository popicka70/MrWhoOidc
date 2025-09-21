using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Roles;

[Authorize]
public class EditModel(AuthDbContext db) : PageModel
{
    public class EditInput
    {
        [Required]
        public Guid RealmId { get; set; }
        [Required, StringLength(100)]
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
    }

    public IReadOnlyList<Realm> Realms { get; private set; } = Array.Empty<Realm>();

    [BindProperty]
    public EditInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        var entity = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null) return RedirectToPage("Index");
        Input = new EditInput { RealmId = entity.RealmId, Name = entity.Name, IsActive = entity.IsActive };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        Realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        if (!ModelState.IsValid) return Page();
        var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null) return RedirectToPage("Index");
        if (!string.Equals(entity.Name, Input.Name, StringComparison.Ordinal))
        {
            var exists = await db.Roles.AnyAsync(r => r.RealmId == entity.RealmId && r.Name == Input.Name && r.Id != id);
            if (exists)
            {
                ModelState.AddModelError("Input.Name", "Role already exists in this realm.");
                return Page();
            }
            entity.Name = Input.Name.Trim();
        }
        entity.IsActive = Input.IsActive;
        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }
}
