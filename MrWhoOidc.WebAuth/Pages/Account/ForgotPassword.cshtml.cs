using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Extensions;

namespace MrWhoOidc.WebAuth.Pages.Account;

/// <summary>
/// Page for requesting a password reset email.
/// Uses global UserAccount for password reset.
/// </summary>
public class ForgotPasswordModel(
    IPasswordResetService passwordResetService,
    ILogger<ForgotPasswordModel> logger) : PageModel
{
    [BindProperty]
    public ForgotPasswordInput Input { get; set; } = new();

    public bool EmailSent { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        logger.LogInformation("🔑 [Password Reset] Reset requested for email {EmailHash}",
            HashForLog(Input.Email!));

        var result = await passwordResetService.CreateResetTokenAsync(
            Input.Email!,
            ipAddress,
            expirationMinutes: 60);

        if (result.Succeeded && result.Token != null)
        {
            // In production, send the token via email
            // For now, we'll just log that we would send it
            // The link's host comes from the configured public base URL, never from the request's Host header:
            // otherwise anyone could request a reset for a victim with a forged Host and receive the token when the
            // victim clicks the link (Host-header poisoning).
            var resetPath = Url.Page(
                "/Account/ResetPassword",
                pageHandler: null,
                values: new { token = result.Token });
            var resetUrl = HttpContext.GetPlatformIssuer().TrimEnd('/') + resetPath;

            logger.LogInformation("Password reset requested for user id {UserIdHash}",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Input.Email ?? ""))));

            var emailService = HttpContext.RequestServices.GetService<IEmailService>();
            if (emailService != null)
            {
                await emailService.SendPasswordResetEmailAsync(result.Account!.Email!, resetUrl!);
            }
        }
        else
        {
            // Don't reveal whether email exists - always show success
            logger.LogDebug("🔑 [Password Reset] No account found for email, but showing success for security");
        }

        // Always show success to prevent email enumeration
        EmailSent = true;
        return Page();
    }

    private static string HashForLog(string value)
    {
        if (string.IsNullOrEmpty(value)) return "[empty]";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"[sha256:{Convert.ToHexString(hash)[..12]}]";
    }

    public sealed class ForgotPasswordInput
    {
        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Please enter a valid email address")]
        [Display(Name = "Email")]
        public string? Email { get; set; }
    }
}
