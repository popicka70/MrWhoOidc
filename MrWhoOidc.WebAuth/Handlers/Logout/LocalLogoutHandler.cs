using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.WebAuth.Infrastructure.Security;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Handles local logout operations (signing out the authentication cookie).
/// Also ends any active tenant support access session during logout.
/// </summary>
public sealed class LocalLogoutHandler(
    ITenantSupportAccessService? supportAccessService = null,
    IUserImpersonationService? userImpersonationService = null)
{
    /// <summary>
    /// Performs a local sign-out and redirects to the return URL.
    /// The return URL is validated to be a local (relative) URL only, preventing open-redirect attacks.
    /// </summary>
    public async Task<IResult> ExecuteAsync(HttpContext http, string? returnUrl)
    {
        // End any active impersonation first: the target OP session and its refresh tokens must not survive
        // the actor's sign-out.
        if (userImpersonationService is not null)
        {
            await userImpersonationService.StopAsync(http).ConfigureAwait(false);
        }

        // End any active support access session
        if (supportAccessService is not null)
        {
            await supportAccessService.StopSupportAccessAsync(http).ConfigureAwait(false);
        }

        // F1: the sign-out ends the OP session and the refresh tokens it produced.
        await LogoutSessionRevoker.RevokeAsync(http, http.User, "the user signed out", http.RequestAborted).ConfigureAwait(false);

        await http.SignOutAsync().ConfigureAwait(false);
        var destination = SanitizeReturnUrl(returnUrl);
        return Results.Redirect(destination);
    }

    /// <summary>
    /// Ensures the return URL is a safe local path. Absolute URLs, protocol-relative URLs
    /// (e.g. //evil.com, /\evil.com) and scheme-prefixed values (e.g. javascript:, data:) are rejected.
    /// </summary>
    private static string SanitizeReturnUrl(string? returnUrl) => SafeRedirect.LocalOrDefault(returnUrl);
}
