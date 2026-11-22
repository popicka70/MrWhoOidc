using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Account.Assistance;

/// <summary>
/// Review, accept or decline one pending assistance request. The invitation token is optional for in-app
/// acceptance: the signed-in helper must always match the invited user, and a token, when supplied, must
/// resolve to the same pending request.
/// </summary>
[Authorize]
public class AcceptModel(
    IUserAssistanceService assistanceService,
    IOptions<AuthOptions> authOptions,
    ILogger<AcceptModel> logger) : PageModel
{
    public UserAssistancePendingInfo? Pending { get; private set; }
    public string? Message { get; private set; }
    public bool FeatureEnabled { get; private set; }

    [BindProperty(SupportsGet = true)]
    public Guid RequestId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public async Task OnGetAsync()
    {
        FeatureEnabled = authOptions.Value.EnableUserAssistance;
        if (!FeatureEnabled)
        {
            Message = "Feature Disabled: user assistance is not enabled.";
            return;
        }

        if (RequestId == Guid.Empty && string.IsNullOrWhiteSpace(Token))
        {
            Message = "The assistance request identifier is missing.";
            return;
        }

        Pending = await assistanceService.GetPendingForAcceptAsync(
            User,
            RequestId,
            Token,
            HttpContext.RequestAborted).ConfigureAwait(false);

        if (Pending is null)
        {
            Message = "This assistance request is invalid, expired, or addressed to a different signed-in user.";
        }
    }

    public async Task<IActionResult> OnPostAcceptAsync()
    {
        FeatureEnabled = authOptions.Value.EnableUserAssistance;
        if (!FeatureEnabled)
        {
            return RedirectToPage();
        }

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var result = await assistanceService.AcceptAsync(
            HttpContext,
            User,
            RequestId,
            Token,
            HttpContext.RequestAborted).ConfigureAwait(false);

        if (!result.Success)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "The assistance request could not be accepted.");
            await OnGetAsync().ConfigureAwait(false);
            return Page();
        }

        TempData["Success"] = "Assistance accepted. You are now acting as the requester; end the session any time.";
        logger.LogWarning(
            "User {HelperUserId} accepted assistance session {SessionId}",
            userId,
            result.SessionId);

        // Land on an overlaid page so the helper immediately sees the requester identity and the banner.
        return Redirect("/account");
    }

    public async Task<IActionResult> OnPostDeclineAsync()
    {
        FeatureEnabled = authOptions.Value.EnableUserAssistance;
        if (!FeatureEnabled)
        {
            return RedirectToPage("/Account/Assistance/Index");
        }

        var result = await assistanceService.DeclineAsync(
            HttpContext,
            User,
            RequestId,
            Token,
            HttpContext.RequestAborted).ConfigureAwait(false);

        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? "Assistance request declined."
            : result.Error ?? "The request could not be declined.";
        return RedirectToPage("/Account/Assistance/Index");
    }
}
