using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Ends the server-side OP session (F1) of a sign-out.
/// <para>
/// The cookie is deleted anyway, but the session is the server-side truth: it is what the relying parties' tokens
/// are bound to. Ending it revokes the refresh tokens the session produced, so a user who signs out is signed out
/// at the relying parties too.
/// </para>
/// </summary>
internal static class LogoutSessionRevoker
{
    /// <summary>
    /// Revokes the session of the given principal (and the refresh tokens it produced).
    /// A failure is logged, never thrown: the sign-out itself must go through.
    /// </summary>
    public static async Task RevokeAsync(
        HttpContext http,
        ClaimsPrincipal? principal,
        string reason,
        CancellationToken ct = default)
    {
        var opSessions = http.RequestServices.GetService<IOpSessionService>();
        if (opSessions is null)
        {
            return;
        }

        var sessionId = OpSessionSignIn.CurrentSessionId(principal);
        if (sessionId is null)
        {
            // A cookie that carries no session (the store is off, or a legacy cookie) has nothing to revoke.
            return;
        }

        try
        {
            await opSessions.RevokeAsync(sessionId.Value, reason, revokeTokens: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("LogoutSessionRevoker");
            logger?.LogWarning(ex, "Could not revoke the OP session {SessionId} during the sign-out", sessionId);
        }
    }
}
