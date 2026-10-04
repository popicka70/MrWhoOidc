namespace MrWhoOidc.WebAuth.Handlers;

/// <summary>
/// Client metadata validation shared by RFC 7591 registration (POST /register) and
/// RFC 7592 client configuration updates (PUT /register/{client_id}), so both paths
/// enforce exactly the same rules.
/// </summary>
internal static class DynamicClientMetadataValidator
{
    /// <summary>
    /// Validates a redirect_uri or post_logout_redirect_uri using an allowlist:
    /// <list type="bullet">
    /// <item><c>https</c> URIs;</item>
    /// <item><c>http</c> only for loopback hosts (RFC 8252 §7.3);</item>
    /// <item>private-use schemes only in reverse-domain form, i.e. containing a '.' (RFC 8252 §7.1).</item>
    /// </list>
    /// Fragments (RFC 6749 §3.1.2) and userinfo components are always rejected.
    /// </summary>
    /// <returns>An error description, or <c>null</c> when the URI is acceptable.</returns>
    public static string? ValidateRedirectUri(string? uri, string parameterName = "redirect_uri")
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return $"Invalid {parameterName}: {uri}";
        }

        if (uri.Contains('#'))
        {
            return $"{parameterName} must not contain a fragment";
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return $"{parameterName} must not contain userinfo";
        }

        var scheme = parsed.Scheme;
        if (string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return parsed.IsLoopback
                ? null
                : $"http {parameterName}s are only allowed for loopback hosts";
        }

        // Private-use URI schemes for native apps must be reverse-domain names (RFC 8252 §7.1).
        // This also rejects javascript:, data:, file:, vbscript: and any other non-app scheme.
        if (scheme.Contains('.'))
        {
            return null;
        }

        return $"{parameterName} scheme '{scheme}' is not allowed";
    }
}
