using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
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

        // Verify user hint matches current user (if applicable)
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var emailClaim = User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value;

        // For login_hint, verify the logged-in user matches the hint
        if (_cibaRequest.HintType == "login_hint" && !string.IsNullOrEmpty(_cibaRequest.UserIdentifierHint))
        {
            // Check if hint matches email or user ID
            var hintMatches = string.Equals(_cibaRequest.UserIdentifierHint, emailClaim, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(_cibaRequest.UserIdentifierHint, userIdClaim, StringComparison.OrdinalIgnoreCase);

            if (!hintMatches)
            {
                ErrorMessage = "This authentication request is for a different user.";
                ShowAuthReqIdInput = true;
                return Page();
            }
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
}
