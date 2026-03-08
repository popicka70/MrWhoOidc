using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Infrastructure.Logging;

namespace MrWhoOidc.WebAuth.Pages.Password;

[Authorize]
public class IndexModel(
    AuthDbContext db,
    IPasswordHasher hasher,
    IPasswordPolicyService passwordPolicy,
    IUserAccountService userAccountService,
    ILogger<IndexModel> logger) : PageModel
{
    [BindProperty]
    public ChangePasswordInput Input { get; set; } = new();

    public bool RequireCurrent { get; private set; }
    public string? SuccessMessage { get; private set; }

    public async Task OnGetAsync()
    {
        var account = await GetCurrentUserAccountAsync();
        RequireCurrent = !string.IsNullOrEmpty(account?.PasswordHash);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var account = await GetCurrentUserAccountAsync();
        if (account is null)
        {
            logger.LogWarning("⚠️ [Password Change] No UserAccount found for authenticated user");
            return RedirectToPage("/Login", new { returnUrl = Url.Page("/Password/Index") });
        }

        RequireCurrent = !string.IsNullOrEmpty(account.PasswordHash);

        if (RequireCurrent && string.IsNullOrEmpty(Input.CurrentPassword))
        {
            ModelState.AddModelError("Input.CurrentPassword", "Current password is required.");
        }

        // Validate new password against tenant password policy
        if (!string.IsNullOrWhiteSpace(Input.NewPassword))
        {
            var validation = await passwordPolicy.ValidatePasswordAsync(Input.NewPassword);
            if (!validation.IsValid)
            {
                foreach (var error in validation.Errors)
                {
                    ModelState.AddModelError("Input.NewPassword", error);
                }
            }
        }
        else
        {
            ModelState.AddModelError("Input.NewPassword", "Password is required.");
        }

        if (!string.Equals(Input.NewPassword, Input.ConfirmPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError("Input.ConfirmPassword", "Passwords do not match.");
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Verify current password against global UserAccount
        if (RequireCurrent && !hasher.Verify(Input.CurrentPassword!, account.PasswordHash!))
        {
            logger.LogWarning("⚠️ [Password Change] Invalid current password for account {AccountId}", account.Id);
            ModelState.AddModelError("Input.CurrentPassword", "Current password is incorrect.");
            return Page();
        }

        // Update password on UserAccount (global credential)
        var newHash = hasher.Hash(Input.NewPassword!);
        logger.LogInformation("🔄 [Password Change] Updating password for account {AccountId}, UsernameHash={UsernameHash}, EmailHash={EmailHash}",
            account.Id,
            LogTokenization.HashId(account.Username),
            LogTokenization.HashId(account.Email));

        await userAccountService.UpdatePasswordAsync(account.Id, newHash, null, "argon2id");

        logger.LogInformation("✅ [Password Change] Password updated successfully for account {AccountId}", account.Id);

        SuccessMessage = "Password updated successfully. This change applies to all your tenants.";
        ModelState.Clear();
        Input = new();
        return Page();
    }

    /// <summary>
    /// Gets the global UserAccount for the current authenticated user.
    /// Looks up via per-tenant User first, then finds the associated UserAccount.
    /// </summary>
    async Task<UserAccount?> GetCurrentUserAccountAsync()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        logger.LogDebug("🔍 [Password] Looking up UserAccount. Sub hash: {SubHash}", LogTokenization.HashId(sub));

        if (!Guid.TryParse(sub, out var userId))
        {
            logger.LogWarning("⚠️ [Password] Invalid sub claim format. Sub hash: {SubHash}", LogTokenization.HashId(sub));
            return null;
        }

        // Get the per-tenant User to find email/username
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            logger.LogWarning("⚠️ [Password] Per-tenant User not found for ID: {UserId}", userId);
            return null;
        }

        logger.LogDebug("🔍 [Password] Found per-tenant User: UsernameHash={UsernameHash}, EmailHash={EmailHash}",
            LogTokenization.HashId(user.Username),
            LogTokenization.HashId(user.Email));

        // Find the global UserAccount by email or username
        if (!string.IsNullOrEmpty(user.Email))
        {
            var account = await userAccountService.FindByEmailAsync(user.Email);
            if (account is not null)
            {
                logger.LogDebug("🔍 [Password] Found UserAccount by email: AccountId={AccountId}, UsernameHash={UsernameHash}",
                    account.Id, LogTokenization.HashId(account.Username));
                return account;
            }
            logger.LogDebug("🔍 [Password] No UserAccount found by email: {EmailHash}", LogTokenization.HashId(user.Email));
        }

        var accountByUsername = await userAccountService.FindByUsernameAsync(user.Username);
        if (accountByUsername is not null)
        {
            logger.LogDebug("🔍 [Password] Found UserAccount by username: AccountId={AccountId}", accountByUsername.Id);
        }
        else
        {
            logger.LogWarning("⚠️ [Password] No UserAccount found for User: UsernameHash={UsernameHash}, EmailHash={EmailHash}",
                LogTokenization.HashId(user.Username),
                LogTokenization.HashId(user.Email));
        }

        return accountByUsername;
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
