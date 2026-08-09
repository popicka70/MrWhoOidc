using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Account;

/// <summary>
/// Page for completing password reset with a valid token.
/// Updates password on global UserAccount.
/// </summary>
public class ResetPasswordModel(
    IPasswordResetService passwordResetService,
    IPasswordPolicyService passwordPolicy,
    ILogger<ResetPasswordModel> logger) : PageModel
{
    [BindProperty]
    public ResetPasswordInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public bool TokenValid { get; set; }
    public bool ResetComplete { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrEmpty(Token))
        {
            ErrorMessage = "Invalid password reset link. Please request a new one.";
            return Page();
        }

        var validation = await passwordResetService.ValidateTokenAsync(Token);
        TokenValid = validation.IsValid;

        if (!validation.IsValid)
        {
            ErrorMessage = validation.ErrorMessage;
            logger.LogWarning("🔑 [Password Reset] Invalid token presented");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrEmpty(Token))
        {
            ErrorMessage = "Invalid password reset link. Please request a new one.";
            return Page();
        }

        // Validate token first
        var validation = await passwordResetService.ValidateTokenAsync(Token);
        if (!validation.IsValid)
        {
            ErrorMessage = validation.ErrorMessage;
            TokenValid = false;
            return Page();
        }

        TokenValid = true;

        // Validate new password
        if (!string.IsNullOrWhiteSpace(Input.NewPassword))
        {
            var policyValidation = await passwordPolicy.ValidatePasswordAsync(Input.NewPassword);
            if (!policyValidation.IsValid)
            {
                foreach (var error in policyValidation.Errors)
                {
                    ModelState.AddModelError("Input.NewPassword", error);
                }
            }
        }

        if (!string.Equals(Input.NewPassword, Input.ConfirmPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError("Input.ConfirmPassword", "Passwords do not match.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Redeem the token and update password
        var result = await passwordResetService.RedeemTokenAsync(Token, Input.NewPassword!);

        if (!result.IsValid)
        {
            ErrorMessage = result.ErrorMessage;
            TokenValid = false;
            logger.LogWarning("🔑 [Password Reset] Failed to redeem token: {Error}", result.ErrorMessage);
            return Page();
        }

        logger.LogInformation("✅ [Password Reset] Password successfully reset for account {AccountId}",
            result.Account?.Id);

        ResetComplete = true;
        return Page();
    }

    public sealed class ResetPasswordInput
    {
        [Required(ErrorMessage = "New password is required")]
        [StringLength(200, MinimumLength = IPasswordPolicyService.DefaultMinLength, ErrorMessage = "Password must be at least 8 characters")]
        [DataType(DataType.Password)]
        [Display(Name = "New Password")]
        public string? NewPassword { get; set; }

        [Required(ErrorMessage = "Please confirm your password")]
        [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match")]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm Password")]
        public string? ConfirmPassword { get; set; }
    }
}
