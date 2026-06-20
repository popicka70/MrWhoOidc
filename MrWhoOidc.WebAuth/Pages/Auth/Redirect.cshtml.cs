using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.DataProtection;

namespace MrWhoOidc.WebAuth.Pages.Auth;

/// <summary>
/// Razor page for performing client-side redirects to relying parties.
/// Used as a workaround for Chromium browsers that don't follow HTTP 302 redirects
/// in certain cross-origin/post-redirect OIDC contexts.
/// </summary>
public class RedirectModel(IDataProtectionProvider dataProtection) : PageModel
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MrWhoOidc.WebAuth.Pages.Auth.Redirect");
    private const string CacheControlValue = "no-store, no-cache, max-age=0";

    /// <summary>
    /// The URL to redirect to.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string RedirectUrl { get; set; } = string.Empty;

    public IActionResult OnGet()
    {
        Response.Headers["Cache-Control"] = CacheControlValue;
        Response.Headers["Pragma"] = "no-cache";

        if (string.IsNullOrWhiteSpace(RedirectUrl))
        {
            return BadRequest("RedirectUrl is required");
        }

        string unprotectedUrl;
        try
        {
            unprotectedUrl = _protector.Unprotect(RedirectUrl);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return BadRequest("Invalid redirect URL");
        }

        // Validate that the URL is well-formed to prevent open redirect attacks.
        // The actual redirect_uri validation happens in AuthorizeHandler before redirecting here.
        if (!Uri.TryCreate(unprotectedUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return BadRequest("Invalid redirect URL");
        }

        RedirectUrl = unprotectedUrl;

        return Page();
    }
}
