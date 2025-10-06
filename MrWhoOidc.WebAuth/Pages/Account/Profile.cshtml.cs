using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account;

[Authorize]
public class ProfileModel(AuthDbContext db) : PageModel
{
    [BindProperty]
    public ProfileInput Input { get; set; } = new();

    public string Username { get; private set; } = string.Empty;
    public string? TenantName { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? SuccessMessage { get; private set; }

    public async Task OnGetAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return;

        Username = user.Username;
        CreatedAt = user.CreatedAt;
        
        Input.Name = user.Name ?? string.Empty;
        Input.Email = user.Email ?? string.Empty;

        // Get tenant name
        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == user.TenantId);
        TenantName = tenant?.Name;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await GetCurrentUserAsync(tracked: true);
        if (user is null) return RedirectToPage("/Login", new { returnUrl = Url.Page("/Account/Profile") });

        if (!ModelState.IsValid)
        {
            Username = user.Username;
            CreatedAt = user.CreatedAt;
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == user.TenantId);
            TenantName = tenant?.Name;
            return Page();
        }

        // Check for duplicate email in same tenant
        var normalizedEmail = Input.Email.ToUpperInvariant();
        var emailExists = await db.Users
            .AnyAsync(u => u.NormalizedEmail == normalizedEmail 
                           && u.TenantId == user.TenantId 
                           && u.Id != user.Id);

        if (emailExists)
        {
            ModelState.AddModelError("Input.Email", "This email is already in use by another user.");
            Username = user.Username;
            CreatedAt = user.CreatedAt;
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == user.TenantId);
            TenantName = tenant?.Name;
            return Page();
        }

        // Update user
        bool emailChanged = user.Email != Input.Email;
        
        user.Name = Input.Name;
        user.Email = Input.Email;
        user.NormalizedEmail = normalizedEmail;

        // If email changed, mark as unverified
        if (emailChanged)
        {
            user.EmailVerified = false;
            user.EmailVerifiedAt = null;
        }

        await db.SaveChangesAsync();

        SuccessMessage = emailChanged 
            ? "Profile updated. Please verify your new email address."
            : "Profile updated successfully.";

        return RedirectToPage();
    }

    private async Task<User?> GetCurrentUserAsync(bool tracked = false)
    {
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId)) return null;

        var query = db.Users.Where(u => u.Id == userId);
        if (!tracked) query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync();
    }

    public sealed class ProfileInput
    {
        [Required, MaxLength(200)]
        [Display(Name = "Full Name")]
        public string Name { get; set; } = string.Empty;

        [Required, EmailAddress, MaxLength(256)]
        [Display(Name = "Email Address")]
        public string Email { get; set; } = string.Empty;
    }
}
