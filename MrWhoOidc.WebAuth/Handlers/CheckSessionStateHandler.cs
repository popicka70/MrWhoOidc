using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

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

    /// <summary>
    /// Compares the relying party's check_session message (<c>client_id + " " + session_state</c>) against the
    /// session the browser state cookie points to: <see cref="CheckSessionState.Unchanged"/> when the session
    /// is alive and the session_state verifies, <see cref="CheckSessionState.Changed"/> when it does not,
    /// <see cref="CheckSessionState.Error"/> when the message cannot be processed.
    /// </summary>
    /// <param name="ctx">The request carrying the browser state cookie.</param>
    /// <param name="receivedMessage">The postMessage payload received by the iframe.</param>
    /// <param name="origin">The origin of the relying party that sent it.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<CheckSessionState> CompareAsync(HttpContext ctx, string? receivedMessage, string? origin, CancellationToken ct = default);
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
    Alive = 2,
    /// <summary>The session is alive and the session_state matches: the relying party keeps the user signed in.</summary>
    Unchanged = 3
}

/// <summary>
/// F1: the check_session iframe asks the server whether the session is still alive. The browser state cookie
/// (<c>__Host-mrwho-opbs</c>) carries the id of the OP session; the answer is the state of that session.
/// <para>
/// The check never touches the session: the relying party polls it, and a poll must not keep an idle session
/// alive.
/// </para>
/// <para>
/// The <see cref="CompareAsync"/> form is what the current iframe uses: it relays the relying party's whole
/// message (<c>client_id + " " + session_state</c>) and its origin, and the server performs the
/// <c>session_state</c> comparison too, so the iframe needs only XHR and postMessage (no WebCrypto, no
/// ES2017). This is the shape that works inside the OIDF conformance suite's scripted browser.
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

    public async Task<CheckSessionState> CompareAsync(
        HttpContext ctx,
        string? receivedMessage,
        string? origin,
        CancellationToken ct = default)
    {
        if (!ctx.Request.Cookies.TryGetValue(OpBrowserStateCookieName, out var opbs) || string.IsNullOrWhiteSpace(opbs))
        {
            // The browser state cookie is gone: the iframe has no session state to compare.
            return CheckSessionState.Error;
        }

        if (!SessionStateCalculator.TryParseMessage(receivedMessage, out var clientId, out var sessionState)
            || string.IsNullOrWhiteSpace(origin))
        {
            return CheckSessionState.Error;
        }

        // The session the cookie points to decides the answer first: an over session is "changed" whatever
        // the session_state says, so a relying party signs the user out even if the browser never came back
        // to the OP (F1). A legacy cookie (a random value, no session id) skips this and relies on the hash.
        if (Guid.TryParse(opbs, out var sessionId))
        {
            var liveness = await GetStateAsync(ctx, ct).ConfigureAwait(false);
            if (liveness == CheckSessionState.Error)
            {
                return CheckSessionState.Error;
            }

            if (liveness == CheckSessionState.Changed)
            {
                return CheckSessionState.Changed;
            }
        }

        if (!SessionStateCalculator.TrySplitSessionState(sessionState!, out var salt, out _))
        {
            return CheckSessionState.Error;
        }

        // The origin comes from the iframe's message event (the relying party's origin); binding it into the
        // hash is what stops a session_state from being replayed against a different relying party.
        var expected = SessionStateCalculator.Compute(clientId!, origin!, opbs, salt);
        return string.Equals(expected, sessionState, StringComparison.Ordinal)
            ? CheckSessionState.Unchanged
            : CheckSessionState.Changed;
    }
}
