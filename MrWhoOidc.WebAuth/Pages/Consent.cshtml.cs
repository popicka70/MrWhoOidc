using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages;

[Authorize]
public class ConsentModel(IConsentService consentService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string ClientId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string[] Scopes { get; set; } = Array.Empty<string>();

    public string CancelUrl => "/"; // could redirect back to app with error

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrEmpty(ReturnUrl))
        {
            return BadRequest("Missing ReturnUrl");
        }

        if (string.IsNullOrEmpty(ClientId))
        {
            return BadRequest("Missing ClientId");
        }

        if (Scopes == null || Scopes.Length == 0)
        {
            return BadRequest("Missing Scopes");
        }

        // Get the current user's ID from claims
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        // Grant consent
        await consentService.GrantConsentAsync(userId, ClientId, Scopes);

        // Redirect back to the authorize endpoint (ReturnUrl already contains the full query string)
        return Redirect(ReturnUrl);
    }
}
