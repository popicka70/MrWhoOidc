using System.Web;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Builds front-channel logout iframe URLs for registered RPs.
/// </summary>
public sealed class FrontChannelLogoutNotifier(ILogger<FrontChannelLogoutNotifier>? logger = null, IHostEnvironment? environment = null)
{
    /// <summary>
    /// Builds front-channel logout iframe URLs for the RPs that took part in the user's session.
    /// </summary>
    public List<string> BuildIframeUrls(string issuer, IReadOnlyList<LogoutTarget> targets)
    {
        var iframes = new List<string>();

        foreach (var target in targets)
        {
            var client = target.Client;
            if (string.IsNullOrEmpty(client.FrontChannelLogoutUri))
            {
                continue;
            }

            var uri = client.FrontChannelLogoutUri;
            if (!IsSafeLogoutUri(uri, environment?.IsDevelopment() == true, out var parsed))
            {
                logger?.LogWarning("Skipping front-channel logout iframe for client {ClientId}: unsafe or invalid FrontChannelLogoutUri", client.ClientId);
                continue;
            }

            var hasQuery = parsed.Query.Length > 0;
            var sep = hasQuery ? '&' : '?';
            var url = uri + sep + "iss=" + Uri.EscapeDataString(issuer);

            if (client.FrontChannelLogoutSessionRequired && !string.IsNullOrEmpty(target.Sid))
            {
                url += "&sid=" + Uri.EscapeDataString(target.Sid);
            }

            iframes.Add(url);
        }

        return iframes;
    }

    /// <summary>
    /// Validates a front-channel logout URI before it is rendered as an iframe src.
    /// Always rejects dangerous schemes (javascript:, data:, file:, vbscript:).
    /// Requires https in non-Development environments; http is only allowed for
    /// localhost in Development. Returns false (and skips the iframe) when unsafe —
    /// skipping is safer than breaking logout for all clients.
    /// </summary>
    private static bool IsSafeLogoutUri(string uri, bool isDevelopment, out Uri parsed)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out parsed!))
        {
            return false;
        }

        var scheme = parsed.Scheme;
        if (string.Equals(scheme, "javascript", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "data", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "vbscript", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // http is only acceptable for localhost in Development environments.
        return string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && isDevelopment
            && IsLocalhostHost(parsed.Host);
    }

    private static bool IsLocalhostHost(string host)
    {
        return host == "localhost"
            || host == "127.0.0.1"
            || host == "[::1]"
            || host.StartsWith("127.", StringComparison.Ordinal)
            || host.StartsWith("[::ffff:127.", StringComparison.Ordinal);
    }
}
