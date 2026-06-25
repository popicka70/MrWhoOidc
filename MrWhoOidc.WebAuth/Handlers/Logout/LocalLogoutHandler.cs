using Microsoft.AspNetCore.Authentication;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Handles local logout operations (signing out the authentication cookie).
/// </summary>
public sealed class LocalLogoutHandler
{
    /// <summary>
    /// Performs a local sign-out and redirects to the return URL.
    /// The return URL is validated to be a local (relative) URL only, preventing open-redirect attacks.
    /// </summary>
    public async Task<IResult> ExecuteAsync(HttpContext http, string? returnUrl)
    {
        await http.SignOutAsync().ConfigureAwait(false);
        var destination = SanitizeReturnUrl(returnUrl);
        return Results.Redirect(destination);
    }

    /// <summary>
    /// Ensures the return URL is a safe local path. Absolute URLs, protocol-relative URLs
    /// (e.g. //evil.com), and scheme-prefixed values (e.g. javascript:, data:) are rejected.
    /// </summary>
    private static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return "/";

        // Block protocol-relative URLs (//evil.com) and scheme-prefixed values
        if (returnUrl.StartsWith("//", StringComparison.Ordinal))
            return "/";

        // Reject absolute URIs (https://evil.com, javascript:alert(1), etc.)
        if (Uri.TryCreate(returnUrl, UriKind.Absolute, out _))
            return "/";

        // Only allow relative URLs that start with / or ~/
        if (!returnUrl.StartsWith("/", StringComparison.Ordinal) &&
            !returnUrl.StartsWith("~/", StringComparison.Ordinal))
            return "/";

        return returnUrl;
    }
}
