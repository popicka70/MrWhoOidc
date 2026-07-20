using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.WebAuth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account.DelegatedAccess;

/// <summary>
/// POST handler for exiting a delegated access context.
/// Clears the active grant reference from the ASP.NET session.
/// The grant remains active and can be re-activated later.
/// Does NOT revoke the grant — only exits the active context.
/// </summary>
[Authorize]
public class ExitModel(
    AuthDbContext db,
    IDelegatedAccessGrantService grantService,
    IDelegatedAccessContextService contextService,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions) : PageModel
{
    public string? Message { get; set; }

    public async Task OnGetAsync()
    {
        // Check feature flag: Delegated Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableDelegatedAccess)
        {
            Message = "Feature Disabled: Delegated Access is not enabled.";
            return;
        }

        // Check if there's an active context to exit
        var activeContext = await contextService.GetActiveContextAsync(HttpContext);
        if (activeContext == null)
        {
            Message = "No delegated context is currently active.";
        }
    }

    public async Task<IActionResult> OnPostExitDelegatedContextAsync()
    {
        // Clear the active grant reference from session
        await contextService.ClearActiveGrantAsync(HttpContext)
            .ConfigureAwait(false);

        TempData["Success"] = "Delegated context exited. You are now acting under your own authority. The grant remains active and can be re-activated later.";
        return RedirectToPage();
    }
}
