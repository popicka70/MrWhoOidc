using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;

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

    [BindProperty(SupportsGet = true)]
    public string ConsentId { get; set; } = string.Empty;

    public string CancelUrl
    {
        get
        {
            if (string.IsNullOrEmpty(ReturnUrl)) return "/";
            try
            {
                // ReturnUrl is a local path like /authorize?client_id=...&redirect_uri=...&state=...
                // Parse redirect_uri and state from it to build an access_denied error redirect.
                var absoluteUri = new Uri("http://local" + ReturnUrl, UriKind.Absolute);
                var query = System.Web.HttpUtility.ParseQueryString(absoluteUri.Query);
                var redirectUri = query["redirect_uri"];
                var state = query["state"];
                if (string.IsNullOrEmpty(redirectUri)) return "/";

                var builder = new UriBuilder(redirectUri);
                var cancelQuery = System.Web.HttpUtility.ParseQueryString(string.Empty);
                cancelQuery["error"] = "access_denied";
                cancelQuery["error_description"] = "The user denied the authorization request";
                if (!string.IsNullOrEmpty(state)) cancelQuery["state"] = state;
                builder.Query = cancelQuery.ToString();
                return builder.ToString();
            }
            catch
            {
                return "/";
            }
        }
    }

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

        // Validate the submitted ClientId and Scopes against the session-stored consent challenge.
        // This prevents an attacker from POST-ing arbitrary client/scope combinations.
        if (string.IsNullOrEmpty(ConsentId))
        {
            return BadRequest("Missing consent challenge");
        }

        var sessionKey = $"consent:{ConsentId}";
        var sessionJson = HttpContext.Session.GetString(sessionKey);
        if (string.IsNullOrEmpty(sessionJson))
        {
            return BadRequest("Invalid or expired consent session");
        }

        var expected = JsonSerializer.Deserialize<JsonElement>(sessionJson);
        var expectedClientId = expected.GetProperty("ClientId").GetString();
        var expectedScopes = expected.GetProperty("Scopes")
            .EnumerateArray()
            .Select(s => s.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        if (!string.Equals(ClientId, expectedClientId, StringComparison.Ordinal))
        {
            return BadRequest("ClientId mismatch");
        }

        var invalidScopes = Scopes.Where(s => !expectedScopes.Contains(s)).ToArray();
        if (invalidScopes.Length > 0)
        {
            return BadRequest("Invalid scopes");
        }

        // Consume the one-time challenge key to prevent replay.
        HttpContext.Session.Remove(sessionKey);

        // Get the current user's ID from claims
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        // Grant consent
        await consentService.GrantConsentAsync(userId, ClientId, Scopes);

        // Redirect back to the authorize endpoint (ReturnUrl already contains the full query string).
        // LocalRedirect rejects any non-local URL, preventing open-redirect attacks.
        var consentReturnUrl = AuthorizeReturnUrlHelper.ConsumePromptValues(ReturnUrl, "consent");
        return LocalRedirect(consentReturnUrl ?? "/");
    }
}
