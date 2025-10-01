using Microsoft.AspNetCore.Authentication;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Handles local logout operations (signing out the authentication cookie).
/// </summary>
public sealed class LocalLogoutHandler
{
    /// <summary>
    /// Performs a local sign-out and redirects to the return URL.
    /// </summary>
    public async Task<IResult> ExecuteAsync(HttpContext http, string? returnUrl)
    {
        await http.SignOutAsync().ConfigureAwait(false);
        var destination = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
        return Results.Redirect(destination);
    }
}
