using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>How recent the session's authentication must be for sensitive account changes.</summary>
public sealed class RecentAuthenticationOptions
{
    public const string SectionName = "Security:RecentAuthentication";

    /// <summary>Maximum age of the session's <c>auth_time</c>. Default 10 minutes.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Step-up check for changes that would let a session thief keep the account (adding a passkey, changing the
/// email address): an authenticated session is not enough, the user must have signed in within
/// <see cref="RecentAuthenticationOptions.MaxAge"/>. The sign-in time is the <c>auth_time</c> claim every
/// interactive sign-in puts into the cookie principal; a session without it counts as not recent.
/// </summary>
public static class RecentAuthentication
{
    public const string ErrorCode = "reauthentication_required";

    // Tolerated clock skew for an auth_time slightly in the future (another node's clock).
    private static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(1);

    public static bool IsRecent(ClaimsPrincipal? user, TimeSpan maxAge, DateTimeOffset now)
    {
        var raw = user?.FindFirst(OidcConstants.Claims.AuthTime)?.Value;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            return false;
        }

        DateTimeOffset authTime;
        try
        {
            authTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return authTime <= now + FutureSkew && now - authTime <= maxAge;
    }

    /// <summary>Checks the current request's user against the configured maximum age.</summary>
    public static bool IsRecent(HttpContext http)
    {
        var maxAge = http.RequestServices?.GetService<IOptions<RecentAuthenticationOptions>>()?.Value.MaxAge
                     ?? new RecentAuthenticationOptions().MaxAge;
        return IsRecent(http.User, maxAge, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 403 for JSON endpoints. The UI sends the user to <paramref name="loginUrl"/> (with a ReturnUrl back to the
    /// page) and retries after the fresh sign-in.
    /// </summary>
    public static IResult ReauthenticationRequired(string loginUrl)
        => Results.Json(
            new
            {
                error = ErrorCode,
                error_description = "Sign in again to confirm this change.",
                login_url = loginUrl,
            },
            statusCode: StatusCodes.Status403Forbidden);
}
