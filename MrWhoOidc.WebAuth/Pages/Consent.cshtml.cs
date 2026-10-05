using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.WebAuth.Services;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Pages;

[Authorize]
public class ConsentModel(
    IConsentService consentService,
    IAuthorizeResponseGenerator responseGenerator,
    IAuthorizeInteractionStore interactionStore) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string ClientId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string[] Scopes { get; set; } = Array.Empty<string>();

    [BindProperty(SupportsGet = true)]
    public string ConsentId { get; set; } = string.Empty;

    public async Task<IActionResult> OnPostDenyAsync()
    {
        // Build the access_denied response only from the server-side consent challenge, whose redirect_uri,
        // state and response_mode were validated at /authorize. ReturnUrl is client-controlled and never used.
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

        HttpContext.Session.Remove(sessionKey);

        var expected = JsonSerializer.Deserialize<JsonElement>(sessionJson);
        string? Read(string name)
            => expected.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var redirectUri = Read("RedirectUri");
        if (string.IsNullOrEmpty(redirectUri))
        {
            return LocalRedirect("/");
        }

        var denied = new AuthorizeValidationResult(
            IsValid: false,
            Error: "access_denied",
            ErrorDescription: "The user denied the authorization request",
            ClientId: Read("ClientId"),
            RedirectUri: redirectUri,
            ResponseMode: Read("ResponseMode"),
            State: Read("State"));

        // The generator adds iss (RFC 9207) and honours the requested response_mode.
        var result = responseGenerator.CreateErrorResponse(HttpContext, denied, HttpContext.TraceIdentifier);
        await result.ExecuteAsync(HttpContext);
        return new EmptyResult();
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

        // A JAR/PAR request keeps prompt=consent in its signed/pushed parameters: record server-side that this
        // browser completed consent for it (only updates an interaction /authorize started in this browser; the
        // regular consent evaluation still runs on the resumed request).
        if (AuthorizeInteractionKey.FromReturnUrl(ReturnUrl) is { } interactionKey)
        {
            await interactionStore.MarkConsentGivenAsync(HttpContext, interactionKey, HttpContext.RequestAborted);
        }

        // Redirect back to the authorize endpoint (ReturnUrl already contains the full query string).
        // LocalRedirect rejects any non-local URL, preventing open-redirect attacks.
        var consentReturnUrl = AuthorizeReturnUrlHelper.ConsumePromptValues(ReturnUrl, "consent");
        return LocalRedirect(consentReturnUrl ?? "/");
    }
}
