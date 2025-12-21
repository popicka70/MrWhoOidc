using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.WebAuth.Pages.Auth;

/// <summary>
/// Razor page for performing client-side redirects to relying parties.
/// Used as a workaround for Chromium browsers that don't follow HTTP 302 redirects
/// in certain cross-origin/post-redirect OIDC contexts.
/// </summary>
public class RedirectModel : PageModel
{
    /// <summary>
    /// The URL to redirect to.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string RedirectUrl { get; set; } = string.Empty;

    public IActionResult OnGet()
    {
        if (string.IsNullOrWhiteSpace(RedirectUrl))
        {
            return BadRequest("RedirectUrl is required");
        }

        // Validate that the URL is well-formed to prevent open redirect attacks.
        // The actual redirect_uri validation happens in AuthorizeHandler before redirecting here.
        if (!Uri.TryCreate(RedirectUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return BadRequest("Invalid redirect URL");
        }

        return Page();
    }
}
