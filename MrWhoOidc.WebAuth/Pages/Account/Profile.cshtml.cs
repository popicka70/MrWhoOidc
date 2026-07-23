using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Models.Delegation;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account;

[Authorize]
public class ProfileModel(
    AuthDbContext db,
    IEffectiveAccessContextAccessor _contextAccessor) : PageModel
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
        var accessContext = await _contextAccessor.GetContextAsync().ConfigureAwait(false);
        if (accessContext.Kind == AccessContextKind.DelegatedAccess)
        {
            return Forbid();
        }

        var user = await GetCurrentUserAsync(tracked: true);
        if (user is null) return RedirectToPage("/login", new { returnUrl = Url.Page("/account/profile") });

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
        var context = await _contextAccessor.GetContextAsync().ConfigureAwait(false);

        if (context.Kind == AccessContextKind.DelegatedAccess)
        {
            return null;
        }

        // Normal access: actor equals subject — use actor ID from context
        var actorUserId = context.ActorUserAccountId;
        var normalQuery = db.Users.Where(u => u.Id == actorUserId);
        if (!tracked) normalQuery = normalQuery.AsNoTracking();
        return await normalQuery.FirstOrDefaultAsync();
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
