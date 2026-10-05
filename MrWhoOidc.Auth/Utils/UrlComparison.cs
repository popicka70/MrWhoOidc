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
/// - Path casing and query are compared exactly; only a trailing slash is tolerated (RFC 9700 §4.1.3)
/// - A requested URL with userinfo, a fragment (RFC 6749 §3.1.2) or dot-segments never matches
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
        // Tolerated: a trailing slash cannot move the redirect off the registered host and path, and seeded
        // post-logout URIs rely on it.
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
        if (!IsExactMatchCandidate(requested)) return false;
        var reqNorm = NormalizeForAllowList(requested);
        var set = new HashSet<string>(allowList
            .Where(a => !string.IsNullOrWhiteSpace(a) && IsValidAbsolute(a))
            .Select(NormalizeForAllowList), StringComparer.Ordinal);
        return set.Contains(reqNorm);
    }

    /// <summary>
    /// Parts that normalisation would otherwise hide: userinfo (https://x@rp/cb matched https://rp/cb), a fragment,
    /// and dot-segments, which Uri resolves before comparison while the raw requested value is what we redirect to.
    /// </summary>
    private static bool IsExactMatchCandidate(string requested)
    {
        if (!Uri.TryCreate(requested.Trim(), UriKind.Absolute, out var u)) return false;
        if (!string.IsNullOrEmpty(u.UserInfo) || requested.Contains('#')) return false;

        var rawPath = requested.Trim();
        var schemeEnd = rawPath.IndexOf("://", StringComparison.Ordinal);
        var pathStart = schemeEnd < 0 ? -1 : rawPath.IndexOf('/', schemeEnd + 3);
        if (pathStart >= 0)
        {
            var queryStart = rawPath.IndexOf('?', pathStart);
            var path = queryStart < 0 ? rawPath[pathStart..] : rawPath[pathStart..queryStart];
            var segments = path.Split('/');
            if (segments.Any(seg => seg is "." or ".." || seg.Equals("%2e", StringComparison.OrdinalIgnoreCase)
                                    || seg.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase) || seg.Equals(".%2e", StringComparison.OrdinalIgnoreCase)
                                    || seg.Equals("%2e.", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }
}
