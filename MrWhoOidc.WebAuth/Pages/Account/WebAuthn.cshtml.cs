using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Persistence;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account;

[Authorize]
public class WebAuthnModel(
    IWebAuthnService webAuthnService,
    ILogger<WebAuthnModel> logger) : PageModel
{
    public IReadOnlyList<WebAuthnCredentialViewModel> Credentials { get; set; } = Array.Empty<WebAuthnCredentialViewModel>();

    public async Task OnGetAsync()
    {
        try
        {
            var userId = GetCurrentUserId();
            if (userId == null)
            {
                logger.LogWarning("User ID not found in claims");
                Credentials = Array.Empty<WebAuthnCredentialViewModel>();
                return;
            }

            var credentials = await webAuthnService.GetUserCredentialsAsync(userId.Value);
            
            Credentials = credentials.Select(c => new WebAuthnCredentialViewModel
            {
                Id = c.Id,
                FriendlyName = c.FriendlyName ?? "Unnamed Key",
                DeviceType = c.DeviceType,
                CreatedAt = c.CreatedAt,
                LastUsedAt = c.LastUsedAt,
                IsActive = c.IsActive
            }).ToArray();

            logger.LogInformation("Retrieved {Count} WebAuthn credentials for user {UserId}", 
                Credentials.Count, userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving WebAuthn credentials");
            Credentials = Array.Empty<WebAuthnCredentialViewModel>();
        }
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}

public class WebAuthnCredentialViewModel
{
    public Guid Id { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string? DeviceType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public bool IsActive { get; set; }
}