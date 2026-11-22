using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Impersonation;

/// <summary>Ends the active real user impersonation session.</summary>
[Authorize(Policy = "platform-admin")]
public class StopModel(IUserImpersonationService impersonationService) : PageModel
{
    public IActionResult OnGet()
        => RedirectToPage("/PlatformAdmin/UserImpersonation/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        var stopped = await impersonationService.StopAsync(HttpContext);
        TempData[stopped ? "Success" : "Error"] = stopped
            ? "Impersonation ended. Any tokens issued to relying parties for the target session were revoked."
            : "There was no active impersonation session to end.";
        return RedirectToPage("/PlatformAdmin/UserImpersonation/Index");
    }
}
