using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Mfa;

[Authorize]
public class IndexModel(
    AuthDbContext db,
    ITotpService totp,
    IMfaCodeVerifier mfaCodes,
    IQrCodeGenerator qrCodeGenerator,
    ITenantSettingsService settingsService,
    IUserAccountService userAccountService,
    ILoginRateLimiter loginRateLimiter,
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

    /// <summary>Status shown on this response only (not carried over in TempData).</summary>
    public string? ResultMessage { get; set; }

    /// <summary>Freshly issued recovery codes; only hashes are stored, so this is the only time they are shown.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; set; }
    public int RemainingRecoveryCodes { get; set; }

    /// <summary>Where to go after saving the recovery codes (required enrolment continues to the TOTP sign-in step).</summary>
    public string? ContinueUrl { get; set; }

    public async Task OnGetAsync()
    {
        var account = await GetCurrentUserAccountAsync();
        if (account is null) { Enabled = false; return; }
        Enabled = account.TotpEnabled;
        if (Enabled)
        {
            RemainingRecoveryCodes = await mfaCodes.CountUnusedRecoveryCodesAsync(account.Id, HttpContext.RequestAborted);
        }

        // Show info banner about global MFA
        InfoBanner = "🔐 MFA settings apply to all your organizations. Once enabled, you'll need to verify your identity when signing in to any organization.";

        if (!Enabled && !string.IsNullOrWhiteSpace(account.TotpSecret))
        {
            SetupPending = true;
            SetProvisioningQr(account.TotpSecret, account.Email ?? account.Username, GetIssuerLabel(), account.TotpAlgorithm);
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
                        SetProvisioningQr(secret, account.Email ?? account.Username, GetIssuerLabel(), TotpAlgorithms.Default);
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
                        if (await mfaCodes.VerifyTotpAsync(account.Id, VerificationCode, HttpContext.RequestAborted))
                        {
                            await userAccountService.ConfirmMfaAsync(account.Id);
                            // H5: rotate the security stamp on MFA enrollment so existing
                            // auth cookies are invalidated on their next request.
                            var trackedAccount = await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == account.Id);
                            if (trackedAccount is not null)
                            {
                                trackedAccount.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                                await db.SaveChangesAsync();
                            }
                            logger.LogInformation("MFA confirmed for UserAccount {AccountId}", account.Id);

                            // Render the recovery codes on this response instead of redirecting: they exist only
                            // here, and the rotated stamp may end this session before another page is shown.
                            RecoveryCodes = await mfaCodes.RegenerateRecoveryCodesAsync(account.Id, HttpContext.RequestAborted);
                            RemainingRecoveryCodes = RecoveryCodes.Count;
                            ResultMessage = "TOTP enabled for all your organizations.";
                            Enabled = true;
                            InfoBanner = "🔐 MFA settings apply to all your organizations.";
                            // Required enrolment continues to the TOTP sign-in step once the codes are saved.
                            ContinueUrl = Required ? Url.Page("/LoginTotp", new { ReturnUrl }) : null;
                            return Page();
                        }
                        else
                        {
                            Message = "Invalid code.";
                            // Regenerate QR for retry
                            SetupPending = true;
                            SetProvisioningQr(totpSecret, account.Email ?? account.Username, GetIssuerLabel(), account.TotpAlgorithm);
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
            case "regenerate-recovery":
                {
                    // New codes invalidate the old ones and reveal working second factors, so they need a current
                    // TOTP code just like disabling does.
                    var (totpActive, _) = await userAccountService.GetMfaStatusAsync(account.Id);
                    Enabled = totpActive;
                    InfoBanner = "🔐 MFA settings apply to all your organizations.";
                    if (!totpActive)
                    {
                        Message = "Enable TOTP before generating recovery codes.";
                        return Page();
                    }

                    var limiterKey = account.Username;
                    if (await loginRateLimiter.IsLockedOutAsync(HttpContext, limiterKey, HttpContext.RequestAborted))
                    {
                        Message = "Too many failed attempts. Please try again later.";
                        return Page();
                    }

                    if (!await mfaCodes.VerifyTotpAsync(account.Id, VerificationCode, HttpContext.RequestAborted))
                    {
                        await loginRateLimiter.RegisterFailedAttemptAsync(HttpContext, limiterKey, HttpContext.RequestAborted);
                        RemainingRecoveryCodes = await mfaCodes.CountUnusedRecoveryCodesAsync(account.Id, HttpContext.RequestAborted);
                        Message = "Enter a current code from your authenticator app to generate new recovery codes.";
                        logger.LogWarning("Recovery code regeneration refused for UserAccount {AccountId}: missing or invalid code", account.Id);
                        return Page();
                    }

                    await loginRateLimiter.ClearAsync(HttpContext, limiterKey, HttpContext.RequestAborted);
                    RecoveryCodes = await mfaCodes.RegenerateRecoveryCodesAsync(account.Id, HttpContext.RequestAborted);
                    RemainingRecoveryCodes = RecoveryCodes.Count;
                    ResultMessage = "New recovery codes generated. Your previous codes no longer work.";
                    logger.LogInformation("Recovery codes regenerated for UserAccount {AccountId}", account.Id);
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

                    // Turning the second factor off needs the second factor. A session alone is not enough: a
                    // hijacked or wrongly linked session (V4) could otherwise strip TOTP from the global account
                    // and enrol the attacker's own authenticator.
                    // (Cancelling a setup that was never confirmed needs no code.)
                    var (totpActive, currentSecret) = await userAccountService.GetMfaStatusAsync(account.Id);
                    if (totpActive && !string.IsNullOrEmpty(currentSecret))
                    {
                        var limiterKey = account.Username;
                        if (await loginRateLimiter.IsLockedOutAsync(HttpContext, limiterKey, HttpContext.RequestAborted))
                        {
                            Enabled = account.TotpEnabled;
                            Message = "Too many failed attempts. Please try again later.";
                            return Page();
                        }

                        if (!await mfaCodes.VerifyTotpAsync(account.Id, VerificationCode, HttpContext.RequestAborted))
                        {
                            await loginRateLimiter.RegisterFailedAttemptAsync(HttpContext, limiterKey, HttpContext.RequestAborted);
                            Enabled = account.TotpEnabled;
                            Message = "Enter a current code from your authenticator app to disable TOTP.";
                            InfoBanner = "🔐 MFA settings apply to all your organizations.";
                            logger.LogWarning("MFA disable refused for UserAccount {AccountId}: missing or invalid code", account.Id);
                            return Page();
                        }

                        await loginRateLimiter.ClearAsync(HttpContext, limiterKey, HttpContext.RequestAborted);
                    }

                    await userAccountService.DisableMfaAsync(account.Id);
                    // H5: rotate the security stamp on MFA disable so existing auth cookies
                    // are invalidated on their next request.
                    var disabledAccount = await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == account.Id);
                    if (disabledAccount is not null)
                    {
                        disabledAccount.SecurityStamp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                        await db.SaveChangesAsync();
                    }
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
        if (user is null)
            return null;

        return await userAccountService.FindForUserAsync(user);
    }

    string GenerateQr(string secret, string account, string issuer, string? storedAlgorithm)
    {
        // A pending enrolment from before the algorithm was stored keeps its SHA256 QR code.
        return totp.GetProvisioningUri(secret, account, issuer, algo: TotpAlgorithms.Resolve(storedAlgorithm));
    }

    string GetIssuerLabel()
    {
        var oidc = HttpContext.RequestServices.GetRequiredService<OidcOptions>();
        return (oidc.Issuer ?? oidc.PublicBaseUrl ?? (Request.Scheme + "://" + Request.Host)).TrimEnd('/');
    }

    void SetProvisioningQr(string secret, string account, string issuer, string? storedAlgorithm)
    {
        QrCodeUri = GenerateQr(secret, account, issuer, storedAlgorithm);
        QrCodeDataUri = qrCodeGenerator.GenerateQrCodeDataUri(QrCodeUri);
        ManualSetupKey = secret;
    }
}
