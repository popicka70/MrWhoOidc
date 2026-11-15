using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// The OIDC Session Management <c>session_state</c> value, issued with every authorization response and
/// verified again by the <c>check_session_iframe</c> state endpoint.
/// <para>
/// The value is <c>hash.salt</c>: the base64url-encoded SHA-256 of
/// <c>client_id + " " + origin + " " + browser_state + " " + salt</c>, with the salt appended verbatim.
/// The <c>browser_state</c> is the value of the <c>__Host-mrwho-opbs</c> cookie (the id of the server-side
/// OP session), and the <c>origin</c> is the origin of the relying party (the redirect_uri origin at issue
/// time, the sender origin at verification time).
/// </para>
/// <para>
/// The hash is computed server-side on both sides: at issue time (this project) and at verification time
/// (the check_session iframe passes the RP's message through to the state endpoint instead of recomputing
/// it in the browser, so the iframe needs no WebCrypto/ES2017 support). The algorithm is identical on both
/// paths, so a value issued here can only verify as <c>unchanged</c> against the same client, origin and
/// browser session.
/// </para>
/// </summary>
public static class SessionStateCalculator
{
    /// <summary>
    /// Computes the session_state for the client at the given origin with the given browser state and salt.
    /// </summary>
    public static string Compute(string clientId, string origin, string browserState, string salt)
    {
        var input = $"{clientId} {origin} {browserState} {salt}";
        var hash = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        return $"{hash}.{salt}";
    }

    /// <summary>
    /// Parses the message a relying party sends to the check_session_iframe:
    /// <c>client_id + " " + session_state</c>. Returns null when it is not exactly that shape.
    /// </summary>
    /// <param name="message">The postMessage payload received by the iframe.</param>
    /// <param name="clientId">The client_id from the message.</param>
    /// <param name="sessionState">The session_state from the message.</param>
    public static bool TryParseMessage(string? message, out string? clientId, out string? sessionState)
    {
        clientId = null;
        sessionState = null;

        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        // client_id may itself contain spaces per RFC 6749 Appendix A.1, so the session_state is the
        // part after the *last* separator; the salt then follows the last '.' within it.
        var separator = message.LastIndexOf(' ');
        if (separator <= 0 || separator == message.Length - 1)
        {
            return false;
        }

        clientId = message[..separator].Trim();
        sessionState = message[(separator + 1)..].Trim();
        return clientId.Length > 0 && sessionState.Length > 0;
    }

    /// <summary>
    /// Splits a session_state into its salt and hash parts. Returns false when there is no '.' separator.
    /// </summary>
    public static bool TrySplitSessionState(string sessionState, out string salt, out string hash)
    {
        salt = string.Empty;
        hash = string.Empty;

        var dot = sessionState.LastIndexOf('.');
        if (dot <= 0 || dot == sessionState.Length - 1)
        {
            return false;
        }

        hash = sessionState[..dot];
        salt = sessionState[(dot + 1)..];
        return true;
    }
}
