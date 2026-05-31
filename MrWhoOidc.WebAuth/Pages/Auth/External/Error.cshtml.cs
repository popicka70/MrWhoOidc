using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;

namespace MrWhoOidc.WebAuth.Pages.Auth.External;

public class ErrorModel : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Cid { get; set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public string? ClientId { get; set; }

    public string? CorrelationId => Cid;

    /// <summary>
    /// A safe, generic user-facing message derived from the stable error <see cref="Code"/>.
    /// Internal/diagnostic messages are never shown to the user; they are logged server-side.
    /// </summary>
    public string Message => FriendlyMessageForCode(Code);

    private static string FriendlyMessageForCode(string? code) => code switch
    {
        "missing_params" => "Your sign-in request was missing required information.",
        "unknown_provider" => "The selected sign-in provider is not available.",
        "provider_not_allowed" => "This sign-in provider isn't permitted for this application.",
        "invalid_provider_config" or "invalid_config" => "The selected sign-in provider is misconfigured. Please contact support.",
        "cid_ref_stale" => "Your sign-in session expired. Please start again.",
        "upstream_error" => "The sign-in provider reported an error. Please try again.",
        "missing_code" => "The sign-in provider did not return the expected response. Please try again.",
        "missing_sub_or_issuer" => "The sign-in provider didn't return enough account information to sign you in.",
        "invalid_discovery_url" or "invalid_discovery_endpoint"
            or "discovery_failed" or "discovery_timeout" or "discovery_exception"
            => "We couldn't reach the sign-in provider. Please try again later.",
        "token_exchange_failed" or "token_timeout" or "token_exception"
            => "We couldn't complete sign-in with the provider. Please try again.",
        "jwks_failed" => "We couldn't verify the sign-in response. Please try again.",
        _ => "Sign-in could not be completed. Please try again, or contact support with the correlation ID below."
    };

    public void OnGet() { }
}
