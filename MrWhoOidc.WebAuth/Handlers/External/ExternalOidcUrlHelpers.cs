using Microsoft.AspNetCore.WebUtilities;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Helper methods for URL manipulation in external OIDC flows.
/// </summary>
internal static class ExternalOidcUrlHelpers
{
    /// <summary>
    /// Ensures the URL contains a cid_ref parameter with the provided handle.
    /// </summary>
    public static string EnsureCidRef(string url, string handle)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(handle))
            return url;

        var fragmentIndex = url.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? url.Substring(fragmentIndex) : string.Empty;
        var basePart = fragmentIndex >= 0 ? url[..fragmentIndex] : url;

        var queryIndex = basePart.IndexOf('?');
        var path = queryIndex >= 0 ? basePart[..queryIndex] : basePart;
        var query = queryIndex >= 0 ? basePart[(queryIndex + 1)..] : string.Empty;

        var parsed = QueryHelpers.ParseQuery(string.IsNullOrEmpty(query) ? string.Empty : "?" + query);
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in parsed)
        {
            if (!string.Equals(kv.Key, "cid_ref", StringComparison.OrdinalIgnoreCase))
            {
                dict[kv.Key] = kv.Value.LastOrDefault();
            }
        }

        dict["cid_ref"] = handle;
        var rebuilt = QueryHelpers.AddQueryString(path, dict);

        return fragment.Length > 0 ? rebuilt + fragment : rebuilt;
    }

    /// <summary>
    /// Extracts the client_id parameter from a return URL.
    /// </summary>
    public static string? TryGetClientIdFromReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return null;

        try
        {
            var ru = new Uri(returnUrl, UriKind.RelativeOrAbsolute);
            var qs = System.Web.HttpUtility.ParseQueryString(
                ru.IsAbsoluteUri ? ru.Query : new Uri("http://local" + returnUrl).Query);
            return qs["client_id"];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies hint parameters from source URL to target dictionary.
    /// </summary>
    public static void CopyHintsFromUrl(string returnUrl, Dictionary<string, string?> targetQuery)
    {
        try
        {
            var ru = new Uri(returnUrl, UriKind.RelativeOrAbsolute);
            var qs = System.Web.HttpUtility.ParseQueryString(
                ru.IsAbsoluteUri ? ru.Query : new Uri("http://local" + returnUrl).Query);

            void TryCopy(string name)
            {
                var val = qs[name];
                if (!string.IsNullOrEmpty(val))
                    targetQuery[name] = val;
            }

            TryCopy("login_hint");
            TryCopy("ui_locales");
            TryCopy("prompt");
            TryCopy("max_age");
            TryCopy("acr_values");
            TryCopy("resource");
            TryCopy("audience");
        }
        catch
        {
            // Ignore parsing errors
        }
    }

    /// <summary>
    /// Checks if a string looks like a valid correlation handle.
    /// </summary>
    public static bool LooksLikeHandle(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8 || value.Length > 64)
            return false;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) continue;
            if (ch is '-' or '_') continue;
            return false;
        }

        return true;
    }
}
