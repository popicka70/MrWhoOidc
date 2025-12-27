using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Sanitizes the browser address bar by removing sensitive or redundant OIDC parameters after they have been processed.
/// </summary>
public interface IAuthorizeRequestSanitizer
{
    /// <summary>
    /// Checks if the current request contains parameters that should be removed from the address bar (e.g., 'request_uri' after PAR resolution).
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <returns>A redirect result to the sanitized URL, or null if no sanitization is needed.</returns>
    IResult? SanitizeAddressBar(HttpContext http);
}
