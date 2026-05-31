using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.ComponentModel.DataAnnotations;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Mfa;

[Authorize]
public class IndexModel(
    AuthDbContext db,
    ITotpService totp,
    IQrCodeGenerator qrCodeGenerator,
    ITenantSettingsService settingsService,
    IUserAccountService userAccountService,
    ILogger<IndexModel> logger) : PageModel
{
    [BindProperty]
    public string? Action { get; set; }

    [BindProperty]
    [StringLength(6, MinimumLength = 6)]
    public string? VerificationCode { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Required { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public bool Enabled { get; set; }
    public bool SetupPending { get; set; }
    public string? QrCodeUri { get; set; }
    public string? QrCodeDataUri { get; set; }
    public string? ManualSetupKey { get; set; }
    public string? Message { get; set; }
    [TempData]
    public string? StatusMessage { get; set; }
    public string? InfoBanner { get; set; }

    public async Task OnGetAsync()
    {
        var account = await GetCurrentUserAccountAsync();
        if (account is null) { Enabled = false; return; }
        Enabled = account.TotpEnabled;

        // Show info banner about global MFA
        InfoBanner = "🔐 MFA settings apply to all your organizations. Once enabled, you'll need to verify your identity when signing in to any organization.";

        if (!Enabled && !string.IsNullOrWhiteSpace(account.TotpSecret))
        {
            SetupPending = true;
            SetProvisioningQr(account.TotpSecret, account.Email ?? account.Username, GetIssuerLabel());
            Message = "Scan QR and confirm with a code.";
        }

        // If MFA is required and user doesn't have it, show warning
        if (Required && !Enabled && !SetupPending)
        {
            Message = "⚠️ Your organization requires multi-factor authentication. Please set up TOTP to continue.";
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var account = await GetCurrentUserAccountAsync();
        if (account is null) return RedirectToPage("/Login");

        switch ((Action ?? string.Empty).ToLowerInvariant())
        {
            case "enable":
                {
                    if (!account.TotpEnabled)
                    {
                        var secret = totp.GenerateSecretBase32();
                        await userAccountService.EnableMfaAsync(account.Id, secret);
                        Enabled = false;
                        SetupPending = true;
                        SetProvisioningQr(secret, account.Email ?? account.Username, GetIssuerLabel());
                        Message = "Scan QR and confirm with a code.";
                        InfoBanner = "🔐 This will enable MFA for all your organizations.";
                        logger.LogInformation("MFA enrollment initiated for UserAccount {AccountId}", account.Id);
                    }
                    else
                    {
                        Enabled = true;
                        Message = "TOTP already enabled.";
                    }
                    return Page();
                }
            case "confirm":
                {
                    var (mfaEnabled, totpSecret) = await userAccountService.GetMfaStatusAsync(account.Id);

                    if (!mfaEnabled && !string.IsNullOrWhiteSpace(totpSecret))
                    {
                        if (!string.IsNullOrWhiteSpace(VerificationCode) && totp.VerifyCode(totpSecret, VerificationCode!, 6, 30, 1))
                        {
                            await userAccountService.ConfirmMfaAsync(account.Id);
                            StatusMessage = "TOTP enabled for all your organizations.";
                            logger.LogInformation("MFA confirmed for UserAccount {AccountId}", account.Id);

                            // If this was required enrollment, redirect to TOTP login page
                            if (Required)
                            {
                                return RedirectToPage("/LoginTotp", new { ReturnUrl });
                            }

                            return RedirectToPage("/Mfa/Index");
                        }
                        else
                        {
                            Message = "Invalid code.";
                            // Regenerate QR for retry
                            SetupPending = true;
                            SetProvisioningQr(totpSecret, account.Email ?? account.Username, GetIssuerLabel());
                        }
                    }
                    else if (mfaEnabled)
                    {
                        StatusMessage = "TOTP is already enabled for all your organizations.";
                        return RedirectToPage("/Mfa/Index");
                    }
                    else
                    {
                        Message = "Start TOTP setup before confirming a code.";
                    }
                    Enabled = mfaEnabled;
                    InfoBanner = "🔐 MFA settings apply to all your organizations.";
                    return Page();
                }
            case "disable":
                {
                    // Check if MFA is required by tenant policy
                    var settings = await settingsService.GetCurrentTenantSettingsAsync();
                    var mfaRequired = settings.Auth?.RequireMfa ?? false;

                    if (mfaRequired)
                    {
                        Enabled = account.TotpEnabled;
                        Message = "⚠️ Cannot disable MFA: Your organization requires multi-factor authentication.";
                        InfoBanner = "🔐 MFA settings apply to all your organizations.";
                        return Page();
                    }

                    await userAccountService.DisableMfaAsync(account.Id);
                    StatusMessage = "TOTP disabled for all your organizations.";
                    logger.LogInformation("MFA disabled for UserAccount {AccountId}", account.Id);
                    return RedirectToPage("/Mfa/Index");
                }
        }

        return RedirectToPage("/Mfa/Index");
    }

    async Task<UserAccount?> GetCurrentUserAccountAsync()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId))
            return null;

        // Get the per-tenant User first to find the linked UserAccount
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || string.IsNullOrEmpty(user.Email))
            return null;

        // Find the UserAccount by email
        return await userAccountService.FindByEmailAsync(user.Email);
    }

    string GenerateQr(string secret, string account, string issuer)
    {
        return totp.GetProvisioningUri(secret, account, issuer);
    }

    string GetIssuerLabel()
    {
        var oidc = HttpContext.RequestServices.GetRequiredService<OidcOptions>();
        return (oidc.Issuer ?? oidc.PublicBaseUrl ?? (Request.Scheme + "://" + Request.Host)).TrimEnd('/');
    }

    void SetProvisioningQr(string secret, string account, string issuer)
    {
        QrCodeUri = GenerateQr(secret, account, issuer);
        QrCodeDataUri = qrCodeGenerator.GenerateQrCodeDataUri(QrCodeUri);
        ManualSetupKey = secret;
    }
}
