using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users;

[Authorize]
public class EditModel(AuthDbContext db) : PageModel
{
    public class EditInput
    {
        [Required, StringLength(200)]
        public string Username { get; set; } = string.Empty;
        [EmailAddress, StringLength(256)]
        public string? Email { get; set; }
        [StringLength(200)]
        public string? Name { get; set; }
    }

    [BindProperty]
    public EditInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return RedirectToPage("Index");
        Input = new EditInput { Username = user.Username, Email = user.Email, Name = user.Name };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!ModelState.IsValid) return Page();
        var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (entity is null) return RedirectToPage("Index");

        var newUsername = Input.Username.Trim();
        if (!string.Equals(entity.Username, newUsername, StringComparison.Ordinal))
        {
            var exists = await db.Users.AnyAsync(u => u.Username == newUsername);
            if (exists)
            {
                ModelState.AddModelError("Input.Username", "Username already exists.");
                return Page();
            }
            entity.Username = newUsername;
        }

        var newEmail = Input.Email?.Trim().ToLowerInvariant();
        if (!string.Equals(entity.Email, newEmail, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(newEmail) && await db.Users.AnyAsync(u => u.Email == newEmail && u.Id != id))
            {
                ModelState.AddModelError("Input.Email", "Email already exists.");
                return Page();
            }
            entity.Email = newEmail;
            entity.EmailVerified = false;
            entity.EmailVerifiedAt = null;
        }

        entity.Name = Input.Name;
        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }
}
