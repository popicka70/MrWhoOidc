using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.ComponentModel.DataAnnotations;
using QRCoder;

namespace MrWhoOidc.WebAuth.Pages.Mfa;

[Authorize]
public class IndexModel(AuthDbContext db, ITotpService totp, IConfiguration config, ITenantSettingsService settingsService) : PageModel
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

    public async Task OnGetAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) { Enabled = false; return; }
        Enabled = user.TotpEnabled;

        // If MFA is required and user doesn't have it, show warning
        if (Required && !Enabled)
        {
            Message = "⚠️ Your organization requires multi-factor authentication. Please set up TOTP to continue.";
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/Login");

        switch ((Action ?? string.Empty).ToLowerInvariant())
        {
            case "enable":
                {
                    if (!user.TotpEnabled)
                    {
                        var secret = totp.GenerateSecretBase32();
                        user.TotpSecret = secret;
                        await db.SaveChangesAsync();
                        Enabled = true;
                        QrPngBase64 = GenerateQr(secret, user.Username, config["Oidc:Issuer"] ?? Request.Scheme + "://" + Request.Host);
                        Message = "Scan QR and confirm with a code.";
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
                    if (!user.TotpEnabled && !string.IsNullOrWhiteSpace(user.TotpSecret))
                    {
                        if (!string.IsNullOrWhiteSpace(VerificationCode) && totp.VerifyCode(user.TotpSecret, VerificationCode!, 6, 30, 1))
                        {
                            user.TotpEnabled = true;
                            await db.SaveChangesAsync();
                            Message = "TOTP enabled.";

                            // If this was required enrollment, redirect to TOTP login page
                            if (Required)
                            {
                                return RedirectToPage("/LoginTotp", new { ReturnUrl });
                            }
                        }
                        else
                        {
                            Message = "Invalid code.";
                        }
                    }
                    Enabled = user.TotpEnabled;
                    return Page();
                }
            case "disable":
                {
                    // Check if MFA is required by tenant policy
                    var settings = await settingsService.GetCurrentTenantSettingsAsync();
                    var mfaRequired = settings.Auth?.RequireMfa ?? false;

                    if (mfaRequired)
                    {
                        Enabled = user.TotpEnabled;
                        Message = "⚠️ Cannot disable MFA: Your organization requires multi-factor authentication.";
                        return Page();
                    }

                    user.TotpEnabled = false;
                    user.TotpSecret = null;
                    await db.SaveChangesAsync();
                    Enabled = false;
                    Message = "TOTP disabled.";
                    return Page();
                }
        }

        return RedirectToPage("/Mfa/Index");
    }

    async Task<User?> GetCurrentUserAsync()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? await db.Users.FirstOrDefaultAsync(u => u.Id == id) : null;
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
