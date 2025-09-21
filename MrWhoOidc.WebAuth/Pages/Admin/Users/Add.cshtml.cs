using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users;

[Authorize]
public class AddModel(AuthDbContext db) : PageModel
{
    public class AddInput
    {
        [Required, StringLength(200)]
        public string Username { get; set; } = string.Empty;
        [EmailAddress, StringLength(256)]
        public string? Email { get; set; }
        [StringLength(200)]
        public string? Name { get; set; }
    }

    [BindProperty]
    public AddInput Input { get; set; } = new();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var username = Input.Username.Trim();
        if (await db.Users.AnyAsync(u => u.Username == username))
        {
            ModelState.AddModelError("Input.Username", "Username already exists.");
            return Page();
        }
        var email = Input.Email?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(email) && await db.Users.AnyAsync(u => u.Email == email))
        {
            ModelState.AddModelError("Input.Email", "Email already exists.");
            return Page();
        }
        db.Users.Add(new User
        {
            Username = username,
            Email = email,
            Name = Input.Name,
            EmailVerified = false,
            HashAlgorithm = "argon2id",
            PasswordHash = string.Empty
        });
        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }
}
