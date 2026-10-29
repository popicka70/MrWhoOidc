using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers;

/// <summary>
/// The answer of the check_session endpoint to the iframe: is the OP session that the browser state cookie
/// points to still alive?
/// </summary>
public interface ICheckSessionStateHandler
{
    /// <summary>
    /// Answers with the state of the session the browser state cookie points to.
    /// </summary>
    Task<CheckSessionState> GetStateAsync(HttpContext ctx, CancellationToken ct = default);
}

/// <summary>
/// The state of the OP session, as the check_session iframe needs it.
/// </summary>
public enum CheckSessionState
{
    /// <summary>The browser state cookie carries no session: the iframe has nothing to compare.</summary>
    Error = 0,
    /// <summary>The session is over (revoked, expired or idle): the relying party must sign the user out.</summary>
    Changed = 1,
    /// <summary>The session is alive: the iframe compares the session state as before.</summary>
    Alive = 2
}

/// <summary>
/// F1: the check_session iframe asks the server whether the session is still alive. The browser state cookie
/// (<c>__Host-mrwho-opbs</c>) carries the id of the OP session; the answer is the state of that session.
/// <para>
/// The check never touches the session: the relying party polls it, and a poll must not keep an idle session
/// alive.
/// </para>
/// </summary>
public sealed class CheckSessionStateHandler(
    IOpSessionService? opSessions = null,
    ILogger<CheckSessionStateHandler>? logger = null) : ICheckSessionStateHandler
{
    /// <summary>The name of the browser state cookie (the "opaque browser session" of OIDC Session Management).</summary>
    public const string OpBrowserStateCookieName = "__Host-mrwho-opbs";

    public async Task<CheckSessionState> GetStateAsync(HttpContext ctx, CancellationToken ct = default)
    {
        if (opSessions is null)
        {
            return CheckSessionState.Alive;
        }

        if (!ctx.Request.Cookies.TryGetValue(OpBrowserStateCookieName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            // The browser state cookie is gone: the iframe has no session state to compare.
            return CheckSessionState.Error;
        }

        if (!Guid.TryParse(raw, out var sessionId))
        {
            // A browser state cookie that is not a session id belongs to an older release (it was a random
            // value). The session state can still be compared, but the server cannot say whether the session
            // is alive.
            return CheckSessionState.Alive;
        }

        try
        {
            var status = await opSessions.GetStatusAsync(sessionId, ct).ConfigureAwait(false);
            return status == OpSessionStatus.Valid ? CheckSessionState.Alive : CheckSessionState.Changed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A database outage must not make every relying party sign the user out: the answer falls back to
            // "the session may still be alive", and the hash comparison decides.
            logger?.LogWarning(ex, "Could not read the state of the OP session {SessionId}", sessionId);
            return CheckSessionState.Alive;
        }
    }
}
