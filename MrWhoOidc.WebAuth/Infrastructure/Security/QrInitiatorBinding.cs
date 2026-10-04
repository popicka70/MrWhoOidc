using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.WebAuth.Infrastructure.Security;

/// <summary>
/// H9: binds a QR login session to the browser that started it. The session token is encoded in the QR code, so
/// anyone it is shown or sent to knows it; the initiator secret lives only in an HttpOnly <c>__Host-</c> cookie on
/// the initiating browser, and only its SHA-256 is stored on the session. Whatever hands the login result to the
/// desktop (status polling with the authorization code, platform sign-in, the QR page with the match code) requires it.
/// </summary>
public static class QrInitiatorBinding
{
    public const string CookiePrefix = "__Host-mrwho-qr-";

    /// <summary>One cookie per session, so two QR logins in two tabs do not overwrite each other.</summary>
    public static string CookieName(string sessionToken) =>
        CookiePrefix + CryptoHelper.ComputeSha256Hex(sessionToken)[..16];

    public static void Issue(HttpContext http, string sessionToken, string initiatorSecret, DateTimeOffset expiresAt)
    {
        var maxAge = expiresAt - DateTimeOffset.UtcNow;
        http.Response.Cookies.Append(CookieName(sessionToken), initiatorSecret, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            // Lax, not Strict: in the OAuth flow the QR page is reached by a redirect chain that a relying party
            // starts cross-site, and that first top-level GET must carry the cookie.
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            MaxAge = maxAge > TimeSpan.Zero ? maxAge : TimeSpan.FromMinutes(1)
        });
    }

    public static void Clear(HttpContext http, string sessionToken) =>
        http.Response.Cookies.Delete(CookieName(sessionToken), new CookieOptions { Secure = true, Path = "/", SameSite = SameSiteMode.Lax });

    /// <summary>
    /// True only when the request carries this session's initiator cookie and its hash matches the stored one
    /// (constant-time). A session without a stored hash (created before the binding existed) is never bound.
    /// </summary>
    public static bool IsBound(HttpContext http, QrLoginSession session)
    {
        if (string.IsNullOrEmpty(session.InitiatorSecretHash) || string.IsNullOrEmpty(session.SessionToken))
            return false;
        if (!http.Request.Cookies.TryGetValue(CookieName(session.SessionToken), out var secret) || string.IsNullOrEmpty(secret))
            return false;

        var actual = Encoding.ASCII.GetBytes(CryptoHelper.ComputeSha256Hex(secret));
        var expected = Encoding.ASCII.GetBytes(session.InitiatorSecretHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Constant-time check of the number the phone user typed against the one shown on the initiating screen.</summary>
    public static bool MatchCodeMatches(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided.Trim()));
    }

    public static QrInitiatorInfo DescribeInitiator(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());

    /// <summary>A short "Browser on OS" description of a user agent, for display only.</summary>
    public static string SummarizeUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "Unknown browser";

        static bool Has(string ua, string token) => ua.Contains(token, StringComparison.OrdinalIgnoreCase);

        var browser =
            Has(userAgent, "Edg/") ? "Edge" :
            Has(userAgent, "OPR/") || Has(userAgent, "Opera") ? "Opera" :
            Has(userAgent, "Firefox/") || Has(userAgent, "FxiOS/") ? "Firefox" :
            Has(userAgent, "Chrome/") || Has(userAgent, "CriOS/") ? "Chrome" :
            Has(userAgent, "Safari/") ? "Safari" :
            "Unknown browser";

        var os =
            Has(userAgent, "Windows") ? "Windows" :
            Has(userAgent, "Android") ? "Android" :
            Has(userAgent, "iPhone") || Has(userAgent, "iPad") ? "iOS" :
            Has(userAgent, "Mac OS X") || Has(userAgent, "Macintosh") ? "macOS" :
            Has(userAgent, "CrOS") ? "ChromeOS" :
            Has(userAgent, "Linux") ? "Linux" :
            null;

        return os is null ? browser : $"{browser} on {os}";
    }
}
