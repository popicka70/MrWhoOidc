using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.ComponentModel.DataAnnotations;
using QRCoder;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.Auth.Options;

namespace MrWhoOidc.WebAuth.Pages.Mfa;

[Authorize]
public class IndexModel(
    AuthDbContext db, 
    ITotpService totp, 
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
    public string? QrPngBase64 { get; set; }
    public string? Message { get; set; }
    public string? InfoBanner { get; set; }

    public async Task OnGetAsync()
    {
        var account = await GetCurrentUserAccountAsync();
        if (account is null) { Enabled = false; return; }
        Enabled = account.TotpEnabled;

        // Show info banner about global MFA
        InfoBanner = "🔐 MFA settings apply to all your organizations. Once enabled, you'll need to verify your identity when signing in to any organization.";

        // If MFA is required and user doesn't have it, show warning
        if (Required && !Enabled)
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
                        Enabled = true;
                        var oidc = HttpContext.RequestServices.GetRequiredService<OidcOptions>();
                        var issuerLabel = (oidc.Issuer ?? oidc.PublicBaseUrl ?? (Request.Scheme + "://" + Request.Host)).TrimEnd('/');
                        QrPngBase64 = GenerateQr(secret, account.Email ?? account.Username, issuerLabel);
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
                    // Reload account to get the pending secret
                    account = await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == account.Id);
                    if (account is null) return RedirectToPage("/Login");

                    if (!account.TotpEnabled && !string.IsNullOrWhiteSpace(account.TotpSecret))
                    {
                        if (!string.IsNullOrWhiteSpace(VerificationCode) && totp.VerifyCode(account.TotpSecret, VerificationCode!, 6, 30, 1))
                        {
                            await userAccountService.ConfirmMfaAsync(account.Id);
                            Message = "✅ TOTP enabled for all your organizations.";
                            logger.LogInformation("MFA confirmed for UserAccount {AccountId}", account.Id);

                            // If this was required enrollment, redirect to TOTP login page
                            if (Required)
                            {
                                return RedirectToPage("/LoginTotp", new { ReturnUrl });
                            }
                        }
                        else
                        {
                            Message = "Invalid code.";
                            // Regenerate QR for retry
                            var oidc = HttpContext.RequestServices.GetRequiredService<OidcOptions>();
                            var issuerLabel = (oidc.Issuer ?? oidc.PublicBaseUrl ?? (Request.Scheme + "://" + Request.Host)).TrimEnd('/');
                            QrPngBase64 = GenerateQr(account.TotpSecret, account.Email ?? account.Username, issuerLabel);
                        }
                    }
                    Enabled = account.TotpEnabled;
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
                    Enabled = false;
                    Message = "TOTP disabled for all your organizations.";
                    InfoBanner = "🔐 MFA settings apply to all your organizations.";
                    logger.LogInformation("MFA disabled for UserAccount {AccountId}", account.Id);
                    return Page();
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
        var uri = totp.GetProvisioningUri(secret, account, issuer);
        var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        var pngBytes = qr.GetGraphic(20);
        return Convert.ToBase64String(pngBytes);
    }
}
