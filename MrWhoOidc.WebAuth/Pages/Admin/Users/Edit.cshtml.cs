using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users;

[Authorize]
public class EditModel(AuthDbContext db) : UserPageModelBase
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
        SetHeading(user.Username, user.Name);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!ModelState.IsValid) return Page();
        var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (entity is null) return RedirectToPage("Index");

        // Initialize heading from current entity state for validation error scenarios.
        SetHeading(entity.Username, entity.Name);

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

        var newEmail = string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email!.Trim();
        var normalized = EmailNormalizer.NormalizeForLookup(newEmail);
        if (!string.Equals(entity.NormalizedEmail, normalized, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(normalized) && await db.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.Id != id))
            {
                ModelState.AddModelError("Input.Email", "Email already exists.");
                return Page();
            }
            entity.Email = newEmail;
            entity.EmailVerified = false;
            entity.EmailVerifiedAt = null;
        }

        entity.Name = string.IsNullOrWhiteSpace(Input.Name) ? null : Input.Name.Trim();
        await db.SaveChangesAsync();
        SetHeading(entity.Username, entity.Name);
        return RedirectToPage("Index");
    }
}
