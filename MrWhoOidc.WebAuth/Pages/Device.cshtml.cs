using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Pages;

/// <summary>
/// Device verification page for RFC 8628 Device Authorization Grant.
/// Users visit this page to enter their user code and authorize devices.
/// </summary>
[Authorize]
public class DeviceModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService,
    ITenantSettingsService settingsService,
    ILogger<DeviceModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "user_code")]
    public string? UserCode { get; set; }

    public string? ErrorMessage { get; set; }
    public string? ClientName { get; set; }
    public string[] RequestedScopes { get; set; } = Array.Empty<string>();

    public bool ShowUserCodeInput { get; set; } = true;
    public bool ShowConfirmation { get; set; }
    public bool ShowSuccess { get; set; }
    public bool ShowDenied { get; set; }

    private DeviceCodeEntry? _deviceCodeEntry;

    public async Task<IActionResult> OnGetAsync()
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;

        if (string.IsNullOrWhiteSpace(UserCode))
        {
            ShowUserCodeInput = true;
            return Page();
        }

        // Normalize user code: remove hyphens, spaces, convert to uppercase
        var normalizedCode = NormalizeUserCode(UserCode);

        // Find the device code entry
        _deviceCodeEntry = await db.DeviceCodes
            .FirstOrDefaultAsync(dc => dc.UserCode == normalizedCode && dc.TenantId == tenantId);

        if (_deviceCodeEntry == null)
        {
            ErrorMessage = "Invalid or expired code. Please check the code and try again.";
            ShowUserCodeInput = true;
            return Page();
        }

        // Check expiration
        if (_deviceCodeEntry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            ErrorMessage = "This code has expired. Please request a new code on your device.";
            ShowUserCodeInput = true;
            return Page();
        }

        // Check if already processed
        if (_deviceCodeEntry.Status != DeviceCodeStatus.Pending)
        {
            ErrorMessage = _deviceCodeEntry.Status == DeviceCodeStatus.Authorized
                ? "This device has already been authorized."
                : "This authorization request has already been processed.";
            ShowUserCodeInput = true;
            return Page();
        }

        // Load client info for display
        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == _deviceCodeEntry.ClientId);

        ClientName = client?.ClientName ?? _deviceCodeEntry.ClientId;

        try
        {
            RequestedScopes = JsonSerializer.Deserialize<string[]>(_deviceCodeEntry.ScopesJson) ?? Array.Empty<string>();
        }
        catch
        {
            RequestedScopes = Array.Empty<string>();
        }

        // Store the normalized code for the form
        UserCode = normalizedCode;
        ShowUserCodeInput = false;
        ShowConfirmation = true;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string action)
    {
        // Approving a device requires a fully-authenticated (post-MFA) session, not just the
        // 5-minute preauth cookie. Otherwise an attacker holding a preauth cookie (e.g. from a
        // stolen password before TOTP was completed) could approve device/CIBA requests.
        var auth = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!auth.Succeeded || auth.Principal?.Identity?.IsAuthenticated != true)
        {
            return RedirectToPage("/Login", new { ReturnUrl = Request.Path + Request.QueryString });
        }

        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;

        if (string.IsNullOrWhiteSpace(UserCode))
        {
            ErrorMessage = "Missing user code.";
            ShowUserCodeInput = true;
            return Page();
        }

        var normalizedCode = NormalizeUserCode(UserCode);

        // Find the device code entry
        _deviceCodeEntry = await db.DeviceCodes
            .FirstOrDefaultAsync(dc => dc.UserCode == normalizedCode && dc.TenantId == tenantId);

        if (_deviceCodeEntry == null)
        {
            ErrorMessage = "Invalid or expired code.";
            ShowUserCodeInput = true;
            return Page();
        }

        if (_deviceCodeEntry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            ErrorMessage = "This code has expired.";
            ShowUserCodeInput = true;
            return Page();
        }

        if (_deviceCodeEntry.Status != DeviceCodeStatus.Pending)
        {
            ErrorMessage = "This authorization request has already been processed.";
            ShowUserCodeInput = true;
            return Page();
        }

        // Get current user ID
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        // An authenticated user whose MFA is not yet satisfied (e.g. tenant now requires MFA, or the
        // user enabled TOTP after the session was established) must complete TOTP before approving.
        // A preauth cookie alone never reaches this point (checked at the top of OnPost).
        if (User.FindFirst("mfa_enrollment_required") is not null || !HasMfaSatisfiedSession())
        {
            var settings = await settingsService.GetCurrentTenantSettingsAsync();
            var mfaRequired = settings.Auth?.RequireMfa ?? false;
            var userEntity = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            var hasTotp = userEntity?.TotpEnabled ?? false;

            if (mfaRequired || hasTotp)
            {
                var returnUrl = Request.Path + Request.QueryString;
                if (hasTotp)
                {
                    // Issue the short-lived preauth cookie so /LoginTotp accepts the challenge
                    // (same pattern as password login).
                    var preauthClaims = new List<Claim>
                    {
                        new(ClaimTypes.NameIdentifier, userId.ToString()),
                        new(ClaimTypes.Name, userEntity?.Username ?? userId.ToString()),
                        new("amr", "ext")
                    };
                    var preauthIdentity = new ClaimsIdentity(preauthClaims, "preauth");
                    await HttpContext.SignInAsync("preauth", new ClaimsPrincipal(preauthIdentity));
                    return Redirect($"/LoginTotp?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
                }

                // Tenant requires MFA but the user has no TOTP yet: force enrollment.
                var enrollClaims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, userId.ToString()),
                    new(ClaimTypes.Name, userEntity?.Username ?? userId.ToString()),
                    new("mfa_enrollment_required", "true")
                };
                var enrollIdentity = new ClaimsIdentity(enrollClaims, "preauth");
                await HttpContext.SignInAsync("preauth", new ClaimsPrincipal(enrollIdentity));
                return Redirect($"/Mfa/Index?required=true&returnUrl={Uri.EscapeDataString(returnUrl)}");
            }
        }

        if (string.Equals(action, "approve", StringComparison.OrdinalIgnoreCase))
        {
            var client = await db.Clients
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.ClientId == _deviceCodeEntry.ClientId && c.TenantId == tenantId);

            if (client?.IsSystemClient == true)
            {
                var tenantAdminResult = await authorizationService.AuthorizeAsync(User, "tenant-admin");
                var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
                if (!tenantAdminResult.Succeeded && !platformAdminResult.Succeeded)
                {
                    ErrorMessage = "CLI access requires administrator privileges for this tenant.";
                    ShowUserCodeInput = true;
                    ShowConfirmation = false;
                    return Page();
                }
            }

            // Authorize the device
            _deviceCodeEntry.Status = DeviceCodeStatus.Authorized;
            _deviceCodeEntry.UserId = userId;
            await db.SaveChangesAsync();

            logger.LogInformation("[Device] User {UserId} authorized device code for client {ClientId}",
                userId, _deviceCodeEntry.ClientId);

            ShowUserCodeInput = false;
            ShowConfirmation = false;
            ShowSuccess = true;
        }
        else
        {
            // Deny the device
            _deviceCodeEntry.Status = DeviceCodeStatus.Denied;
            await db.SaveChangesAsync();

            logger.LogInformation("[Device] User {UserId} denied device code for client {ClientId}",
                userId, _deviceCodeEntry.ClientId);

            ShowUserCodeInput = false;
            ShowConfirmation = false;
            ShowDenied = true;
        }

        return Page();
    }

    /// <summary>
    /// A session counts as MFA-satisfied when it carries the "mfa" amr or the MFA acr value
    /// (both set by LoginTotp after a successful TOTP challenge; WebAuthn passkeys count via
    /// their own amr/acr and are accepted here too since passkey verification is a second factor).
    /// </summary>
    private bool HasMfaSatisfiedSession()
    {
        if (User.HasClaim(claim => claim.Type == MrWhoOidc.Auth.Protocols.OidcConstants.Claims.Amr
                && claim.Value.Equals("mfa", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var acr = User.FindFirst(MrWhoOidc.Auth.Protocols.OidcConstants.Claims.Acr)?.Value;
        return string.Equals(acr, MrWhoOidc.Auth.Protocols.OidcConstants.AcrValues.Mfa, StringComparison.Ordinal)
            || string.Equals(acr, MrWhoOidc.Auth.Protocols.OidcConstants.AcrValues.Passkey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalizes user code by removing hyphens, spaces, and converting to uppercase.
    /// </summary>
    private static string NormalizeUserCode(string code)
    {
        return code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
    }
}
