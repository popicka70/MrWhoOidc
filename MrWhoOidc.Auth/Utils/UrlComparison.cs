namespace MrWhoOidc.Auth.Utils;

/// <summary>
/// Central helper for normalizing and comparing redirect / post-logout URLs.
/// Rules:
/// - Must be absolute URI to be considered valid
/// - Comparison ignores query string and fragment
/// - Scheme + host are lowercased
/// - Default port is omitted; non-default preserved
/// - Path always starts with '/'
/// - Trailing slash removed unless path is root ('/')
/// - Path casing preserved (case sensitive portion)
/// </summary>
public static class UrlComparison
{
    public static bool IsValidAbsolute(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out _);

    /// <summary>Normalize an absolute URL for allow-list comparison. If invalid, returns original trimmed input.</summary>
    public static string NormalizeForAllowList(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return string.Empty;
        uri = uri.Trim();
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return uri;
        var scheme = u.Scheme.ToLowerInvariant();
        var host = u.Host.ToLowerInvariant();
        var portPart = u.IsDefaultPort ? string.Empty : ":" + u.Port;
        var path = string.IsNullOrEmpty(u.AbsolutePath) ? "/" : u.AbsolutePath;
        if (!path.StartsWith('/')) path = "/" + path; // safety
        if (path.Length > 1 && path.EndsWith('/')) path = path.TrimEnd('/');
        return scheme + "://" + host + portPart + path;
    }

    /// <summary>Returns true if requested URL (ignoring query/fragment) matches any URL in the allow-list after normalization.</summary>
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
