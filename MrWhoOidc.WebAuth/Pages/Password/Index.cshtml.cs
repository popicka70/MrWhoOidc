using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Password;

[Authorize]
public class IndexModel(AuthDbContext db, IPasswordHasher hasher) : PageModel
{
    [BindProperty]
    public ChangePasswordInput Input { get; set; } = new();

    public bool RequireCurrent { get; private set; }
    public string? SuccessMessage { get; private set; }

    public async Task OnGetAsync()
    {
        var user = await GetCurrentUserAsync();
        RequireCurrent = !string.IsNullOrEmpty(user?.PasswordHash);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/Login", new { returnUrl = Url.Page("/Password/Index") });

        RequireCurrent = !string.IsNullOrEmpty(user.PasswordHash);

        if (RequireCurrent && string.IsNullOrEmpty(Input.CurrentPassword))
        {
            ModelState.AddModelError("Input.CurrentPassword", "Current password is required.");
        }
        if (string.IsNullOrWhiteSpace(Input.NewPassword) || Input.NewPassword.Length < 6)
        {
            ModelState.AddModelError("Input.NewPassword", "Password must be at least 6 characters.");
        }
        if (!string.Equals(Input.NewPassword, Input.ConfirmPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError("Input.ConfirmPassword", "Passwords do not match.");
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (RequireCurrent && !hasher.Verify(Input.CurrentPassword!, user.PasswordHash!))
        {
            ModelState.AddModelError("Input.CurrentPassword", "Current password is incorrect.");
            return Page();
        }

        user.PasswordHash = hasher.Hash(Input.NewPassword!);
        user.HashAlgorithm = "argon2id";
        await db.SaveChangesAsync();

        SuccessMessage = "Password updated successfully.";
        ModelState.Clear();
        Input = new();
        return Page();
    }

    async Task<User?> GetCurrentUserAsync()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? await db.Users.FirstOrDefaultAsync(u => u.Id == id) : null;
    }

    public sealed class ChangePasswordInput
    {
        [DataType(DataType.Password)]
        public string? CurrentPassword { get; set; }
        [Required, StringLength(200, MinimumLength = 6)]
        [DataType(DataType.Password)]
        public string? NewPassword { get; set; }
        [Required, Compare(nameof(NewPassword))]
        [DataType(DataType.Password)]
        public string? ConfirmPassword { get; set; }
    }
}
