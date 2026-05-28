using System;
using System.Collections.Generic;
using System.Linq;

namespace MrWhoOidc.Auth.Utils;

/// <summary>
/// Central helper for normalizing and comparing redirect / post-logout URLs.
/// Rules:
/// - Must be absolute URI to be considered valid
/// - Scheme + host are lowercased
/// - Default port is omitted; non-default preserved
/// - Path always starts with '/'
/// - Trailing slash removed unless path is root ('/')
/// - Path casing preserved (case sensitive portion)
/// - Query string and fragment are preserved and included in comparison
/// </summary>
public static class UrlComparison
{
    // Only these schemes are valid for redirect / post-logout URLs. This blocks
    // dangerous schemes (javascript:, data:, file:, etc.) from ever entering the
    // allow-list or being accepted as a requested redirect target.
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "https", "http" };

    public static bool IsValidAbsolute(string uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var u)
           && AllowedSchemes.Contains(u.Scheme)
           && u.Port is > 0 and <= 65535;

    /// <summary>Normalize an absolute URL for allow-list comparison. If invalid, returns original trimmed input.</summary>
    public static string NormalizeForAllowList(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return string.Empty;
        uri = uri.Trim();
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return uri;
        if (!AllowedSchemes.Contains(u.Scheme)) return uri;
        var scheme = u.Scheme.ToLowerInvariant();
        var host = u.Host.ToLowerInvariant();
        var portPart = u.IsDefaultPort ? string.Empty : ":" + u.Port;
        var path = string.IsNullOrEmpty(u.AbsolutePath) ? "/" : u.AbsolutePath;
        if (!path.StartsWith('/')) path = "/" + path; // safety
        if (path.Length > 1 && path.EndsWith('/')) path = path.TrimEnd('/');

        // Security fix: Include query and fragment in comparison to prevent open redirect attacks
        // and enforce strict matching per OAuth 2.0 Security Best Practices.
        var query = u.Query;
        var fragment = u.Fragment;

        return scheme + "://" + host + portPart + path + query + fragment;
    }

    /// <summary>Returns true if requested URL matches any URL in the allow-list after normalization.</summary>
    public static bool IsAllowed(string requested, IEnumerable<string> allowList)
    {
        if (string.IsNullOrWhiteSpace(requested)) return false;
        var reqNorm = NormalizeForAllowList(requested);
        var set = new HashSet<string>(allowList
            .Where(a => !string.IsNullOrWhiteSpace(a) && IsValidAbsolute(a))
            .Select(NormalizeForAllowList), StringComparer.Ordinal);
        return set.Contains(reqNorm);
    }
}
