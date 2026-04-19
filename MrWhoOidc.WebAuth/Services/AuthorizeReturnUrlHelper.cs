using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.WebAuth.Services;

internal static class AuthorizeReturnUrlHelper
{
    private static readonly object RequestParametersItemKey = new();
    private static readonly object LocalAuthorizeReturnUrlItemKey = new();

    public static async Task<IReadOnlyList<KeyValuePair<string, string>>> GetRequestParametersAsync(HttpContext http)
    {
        if (http.Items.TryGetValue(RequestParametersItemKey, out var cached)
            && cached is IReadOnlyList<KeyValuePair<string, string>> existing)
        {
            return existing;
        }

        var merged = new List<KeyValuePair<string, string>>();
        AppendValues(merged, http.Request.Query);

        if (HttpMethods.IsPost(http.Request.Method) && http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
            foreach (var key in form.Keys)
            {
                merged.RemoveAll(pair => string.Equals(pair.Key, key, StringComparison.Ordinal));
                AppendValues(merged, key, form[key]);
            }
        }

        var snapshot = merged.ToArray();
        http.Items[RequestParametersItemKey] = snapshot;
        return snapshot;
    }

    public static string? GetParameterValue(IReadOnlyList<KeyValuePair<string, string>> parameters, string key)
    {
        for (var index = parameters.Count - 1; index >= 0; index--)
        {
            if (string.Equals(parameters[index].Key, key, StringComparison.Ordinal))
            {
                return parameters[index].Value;
            }
        }

        return null;
    }

    public static QueryString BuildQueryString(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var builder = new QueryBuilder();
        foreach (var parameter in parameters)
        {
            builder.Add(parameter.Key, parameter.Value);
        }

        return builder.ToQueryString();
    }

    public static string BuildLocalAuthorizeReturnUrl(PathString path, IEnumerable<KeyValuePair<string, string>> parameters)
        => (path.HasValue ? path.Value : "/") + BuildQueryString(parameters).ToUriComponent();

    public static async Task<string> GetOrCreateLocalAuthorizeReturnUrlAsync(HttpContext http)
    {
        if (TryGetStoredLocalAuthorizeReturnUrl(http, out var existing))
        {
            return existing;
        }

        var parameters = await GetRequestParametersAsync(http).ConfigureAwait(false);
        var returnUrl = BuildLocalAuthorizeReturnUrl(http.Request.Path, parameters);
        SetLocalAuthorizeReturnUrl(http, returnUrl);
        return returnUrl;
    }

    public static void SetLocalAuthorizeReturnUrl(HttpContext http, string returnUrl)
        => http.Items[LocalAuthorizeReturnUrlItemKey] = returnUrl;

    public static bool TryGetStoredLocalAuthorizeReturnUrl(HttpContext http, out string returnUrl)
    {
        if (http.Items.TryGetValue(LocalAuthorizeReturnUrlItemKey, out var cached)
            && cached is string value
            && !string.IsNullOrWhiteSpace(value))
        {
            returnUrl = value;
            return true;
        }

        returnUrl = string.Empty;
        return false;
    }

    public static string GetStoredLocalAuthorizeReturnUrlOrCurrentRequest(HttpContext http)
    {
        if (TryGetStoredLocalAuthorizeReturnUrl(http, out var stored))
        {
            return stored;
        }

        var currentParameters = http.Request.Query.SelectMany(
            pair => pair.Value,
            static (pair, value) => new KeyValuePair<string, string>(pair.Key, value ?? string.Empty));

        return BuildLocalAuthorizeReturnUrl(http.Request.Path, currentParameters);
    }

    public static string? ConsumePromptValues(string? returnUrl, params string[] consumedPrompts)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || consumedPrompts.Length == 0)
        {
            return returnUrl;
        }

        if (!IsLocalUrl(returnUrl))
        {
            return returnUrl;
        }

        var normalizedLocalUrl = returnUrl[0] == '~' ? returnUrl[1..] : returnUrl;
        if (!Uri.TryCreate("http://local" + normalizedLocalUrl, UriKind.Absolute, out var absoluteUri))
        {
            return returnUrl;
        }

        if (!absoluteUri.AbsolutePath.EndsWith("/authorize", StringComparison.OrdinalIgnoreCase))
        {
            return returnUrl;
        }

        var parsedQuery = QueryHelpers.ParseQuery(absoluteUri.Query);
        if (!parsedQuery.TryGetValue(OidcConstants.Parameters.Prompt, out var promptValues))
        {
            return returnUrl;
        }

        var consumedSet = new HashSet<string>(consumedPrompts, StringComparer.OrdinalIgnoreCase);
        var remainingPrompts = new List<string>();
        var removed = false;

        foreach (var prompt in promptValues
            .SelectMany(static value => (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (consumedSet.Contains(prompt))
            {
                removed = true;
                continue;
            }

            if (!remainingPrompts.Contains(prompt, StringComparer.OrdinalIgnoreCase))
            {
                remainingPrompts.Add(prompt);
            }
        }

        if (!removed)
        {
            return returnUrl;
        }

        var builder = new QueryBuilder();
        foreach (var pair in parsedQuery)
        {
            if (string.Equals(pair.Key, OidcConstants.Parameters.Prompt, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var value in pair.Value)
            {
                builder.Add(pair.Key, value ?? string.Empty);
            }
        }

        if (remainingPrompts.Count > 0)
        {
            builder.Add(OidcConstants.Parameters.Prompt, string.Join(' ', remainingPrompts));
        }

        var rebuiltUrl = absoluteUri.AbsolutePath + builder.ToQueryString().ToUriComponent();
        if (!string.IsNullOrEmpty(absoluteUri.Fragment))
        {
            rebuiltUrl += absoluteUri.Fragment;
        }

        return rebuiltUrl;
    }

    private static void AppendValues(List<KeyValuePair<string, string>> target, IEnumerable<KeyValuePair<string, StringValues>> source)
    {
        foreach (var pair in source)
        {
            AppendValues(target, pair.Key, pair.Value);
        }
    }

    private static void AppendValues(List<KeyValuePair<string, string>> target, string key, StringValues values)
    {
        foreach (var value in values)
        {
            target.Add(new KeyValuePair<string, string>(key, value ?? string.Empty));
        }
    }

    private static bool IsLocalUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        if (url[0] == '/')
        {
            return url.Length == 1 || (url[1] != '/' && url[1] != '\\');
        }

        if (url[0] == '~' && url.Length > 1 && url[1] == '/')
        {
            return url.Length == 2 || (url[2] != '/' && url[2] != '\\');
        }

        return false;
    }
}