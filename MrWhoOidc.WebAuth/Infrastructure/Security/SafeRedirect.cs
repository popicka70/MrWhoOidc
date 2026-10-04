using System.Diagnostics.CodeAnalysis;

namespace MrWhoOidc.WebAuth.Infrastructure.Security;

/// <summary>
/// Strict validation for user-supplied post-login/logout return URLs (open-redirect defence).
/// Only same-origin absolute paths are accepted: the value must start with a single <c>/</c>, must not
/// be protocol-relative (<c>//host</c>, <c>/\host</c>), must not contain a backslash anywhere (browsers
/// treat <c>\</c> as <c>/</c>) and must not contain control characters (browsers strip tab/CR/LF, so
/// <c>/\t/evil.com</c> becomes <c>//evil.com</c>). Schemes (<c>https:</c>, <c>javascript:</c>), bare hosts
/// (<c>evil.com</c>) and <c>~/</c> paths are rejected.
/// </summary>
public static class SafeRedirect
{
    public static bool IsSafeLocalPath([NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
        {
            return false;
        }

        if (url.Length > 1 && (url[1] == '/' || url[1] == '\\'))
        {
            return false;
        }

        foreach (var ch in url)
        {
            if (ch == '\\' || char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns <paramref name="url"/> when it is a safe local path, otherwise <paramref name="fallback"/>.</summary>
    public static string LocalOrDefault(string? url, string fallback = "/")
        => IsSafeLocalPath(url) ? url : fallback;
}
