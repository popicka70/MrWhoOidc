using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Account.Assistance;

/// <summary>
/// Control endpoint for ending assistance from either party. It deliberately runs without an overlay:
/// the helper may end their own session through the browser session key, and the requester/manager may end
/// a specific session by id after the service re-checks who is allowed to end it.
/// </summary>
[Authorize]
public class StopModel(IUserAssistanceService assistanceService) : PageModel
{
    public IActionResult OnGet()
        => RedirectToPage("/Account/Assistance/Index");

    public async Task<IActionResult> OnPostAsync(Guid? sessionId, string? reason)
    {
        var result = sessionId is { } id
            ? await assistanceService.EndAsync(
                HttpContext,
                User,
                id,
                reason ?? "Ended",
                HttpContext.RequestAborted).ConfigureAwait(false)
            : await assistanceService.StopAsync(HttpContext, HttpContext.RequestAborted).ConfigureAwait(false);

        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? "Assistance ended. The target access and its tokens were revoked."
            : result.Error ?? "There was no active assistance session to end.";
        return RedirectToPage("/Account/Assistance/Index");
    }
}
