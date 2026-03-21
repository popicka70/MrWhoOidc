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
    /// Normalizes user code by removing hyphens, spaces, and converting to uppercase.
    /// </summary>
    private static string NormalizeUserCode(string code)
    {
        return code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
    }
}
