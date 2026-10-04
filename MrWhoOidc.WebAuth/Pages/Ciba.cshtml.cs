using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Services;
using System.Security.Claims;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Pages;

/// <summary>
/// CIBA consent page for OpenID Connect CIBA Core 1.0.
/// Users visit this page (typically via push notification link) to approve/deny authentication requests.
/// </summary>
[Authorize]
public class CibaModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IOptions<AuthOptions> authOptions,
    ITenantSettingsService settingsService,
    ICibaNotificationService notificationService,
    ILogger<CibaModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? AuthReqId { get; set; }

    public string? ErrorMessage { get; set; }
    public string? ClientName { get; set; }
    public string? BindingMessage { get; set; }
    public string[] RequestedScopes { get; set; } = Array.Empty<string>();

    public bool ShowAuthReqIdInput { get; set; } = true;
    public bool ShowConfirmation { get; set; }
    public bool ShowSuccess { get; set; }
    public bool ShowDenied { get; set; }
    public bool CibaEnabled => authOptions.Value.EnableCiba;

    private CibaAuthenticationRequest? _cibaRequest;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!CibaEnabled)
        {
            return NotFound();
        }

        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;

        if (string.IsNullOrWhiteSpace(AuthReqId))
        {
            ShowAuthReqIdInput = true;
            return Page();
        }

        // Find the CIBA request entry
        _cibaRequest = await db.CibaAuthenticationRequests
            .FirstOrDefaultAsync(r => r.AuthReqId == AuthReqId && r.TenantId == tenantId);

        if (_cibaRequest == null)
        {
            ErrorMessage = "Invalid or expired authentication request.";
            ShowAuthReqIdInput = true;
            return Page();
        }

        // Check expiration
        if (_cibaRequest.ExpiresAt < DateTimeOffset.UtcNow)
        {
            ErrorMessage = "This authentication request has expired.";
            ShowAuthReqIdInput = true;
            return Page();
        }

        // Check if already processed
        if (_cibaRequest.Status != CibaRequestStatus.Pending)
        {
            ErrorMessage = _cibaRequest.Status == CibaRequestStatus.Authorized
                ? "This request has already been authorized."
                : "This authorization request has already been processed.";
            ShowAuthReqIdInput = true;
            return Page();
        }

        // Only the user the request was issued for may see or act on it.
        if (!IsRequestedUser(_cibaRequest))
        {
            ErrorMessage = "This authentication request is for a different user.";
            ShowAuthReqIdInput = true;
            return Page();
        }

        // Load client info for display
        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == _cibaRequest.ClientId);

        ClientName = client?.ClientName ?? _cibaRequest.ClientId;
        BindingMessage = _cibaRequest.BindingMessage;

        try
        {
            RequestedScopes = JsonSerializer.Deserialize<string[]>(_cibaRequest.ScopesJson) ?? Array.Empty<string>();
        }
        catch
        {
            RequestedScopes = Array.Empty<string>();
        }

        ShowAuthReqIdInput = false;
        ShowConfirmation = true;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string action)
    {
        if (!CibaEnabled)
        {
            return NotFound();
        }

        // Approving a CIBA request requires a fully-authenticated (post-MFA) session, not just the
        // 5-minute preauth cookie. Otherwise an attacker holding a preauth cookie (e.g. from a
        // stolen password before TOTP was completed) could approve CIBA requests.
        var auth = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!auth.Succeeded || auth.Principal?.Identity?.IsAuthenticated != true)
        {
            return RedirectToPage("/Login", new { ReturnUrl = Request.Path + Request.QueryString });
        }

        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? Guid.Empty;

        if (string.IsNullOrWhiteSpace(AuthReqId))
        {
            ErrorMessage = "Missing authentication request ID.";
            ShowAuthReqIdInput = true;
            return Page();
        }

        // Find the CIBA request entry
        _cibaRequest = await db.CibaAuthenticationRequests
            .FirstOrDefaultAsync(r => r.AuthReqId == AuthReqId && r.TenantId == tenantId);

        if (_cibaRequest == null)
        {
            ErrorMessage = "Invalid or expired authentication request.";
            ShowAuthReqIdInput = true;
            return Page();
        }

        if (_cibaRequest.ExpiresAt < DateTimeOffset.UtcNow)
        {
            ErrorMessage = "This authentication request has expired.";
            ShowAuthReqIdInput = true;
            return Page();
        }

        if (_cibaRequest.Status != CibaRequestStatus.Pending)
        {
            ErrorMessage = "This authorization request has already been processed.";
            ShowAuthReqIdInput = true;
            return Page();
        }

        // Get current user ID
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        // Bind approval to the user the request targets: without this, any signed-in user who learned an
        // auth_req_id could approve it and the client would receive tokens for that user instead.
        if (!IsRequestedUser(_cibaRequest))
        {
            logger.LogWarning("[CIBA] Rejected approval by non-target user for authReqId={AuthReqId}", _cibaRequest.AuthReqId);
            ErrorMessage = "This authentication request is for a different user.";
            ShowAuthReqIdInput = true;
            return Page();
        }

        // An authenticated user whose MFA is not yet satisfied (e.g. tenant now requires MFA, or the
        // user enabled TOTP after the session was established) must complete TOTP before approving.
        // A preauth cookie alone never reaches this point (checked at the top of OnPost).
        if (User.FindFirst("mfa_enrollment_required") is not null || !HasMfaSatisfiedSession())
        {
            var settings = await settingsService.GetCurrentTenantSettingsAsync();
            var mfaRequired = settings.Auth?.RequireMfa ?? false;
            var userEntity = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            var hasTotp = userEntity is not null && await MfaState.HasTotpAsync(HttpContext, userEntity);

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
            // Authorize the request
            _cibaRequest.Status = CibaRequestStatus.Authorized;
            _cibaRequest.UserId = userId;
            await db.SaveChangesAsync();

            logger.LogInformation("[CIBA] User {UserId} authorized CIBA request for client {ClientId} authReqId={AuthReqId}",
                userId, _cibaRequest.ClientId, _cibaRequest.AuthReqId);

            // Send ping notification if configured (for ping mode)
            if (!string.IsNullOrEmpty(_cibaRequest.ClientNotificationToken) && !_cibaRequest.PingNotificationSent)
            {
                try
                {
                    await notificationService.SendPingNotificationAsync(_cibaRequest);
                    _cibaRequest.PingNotificationSent = true;
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[CIBA] Failed to send ping notification for authReqId={AuthReqId}", _cibaRequest.AuthReqId);
                }
            }

            ShowAuthReqIdInput = false;
            ShowConfirmation = false;
            ShowSuccess = true;
        }
        else
        {
            // Deny the request
            _cibaRequest.Status = CibaRequestStatus.Denied;
            await db.SaveChangesAsync();

            logger.LogInformation("[CIBA] User {UserId} denied CIBA request for client {ClientId} authReqId={AuthReqId}",
                userId, _cibaRequest.ClientId, _cibaRequest.AuthReqId);

            ShowAuthReqIdInput = false;
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

    /// <summary>The request stores the resolved target user's id (see CibaAuthenticationHandler).</summary>
    private bool IsRequestedUser(CibaAuthenticationRequest request)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return !string.IsNullOrEmpty(userIdClaim)
            && string.Equals(request.UserIdentifierHint, userIdClaim, StringComparison.OrdinalIgnoreCase);
    }
}
