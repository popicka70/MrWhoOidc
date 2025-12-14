using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Extensions;

namespace MrWhoOidc.WebAuth.Services;

public interface IReturnUrlClientContextResolver
{
    Task<Client?> TryResolveClientAsync(HttpContext http, string? returnUrl, CancellationToken ct = default);
}

internal sealed class ReturnUrlClientContextResolver(
    IAuthorizeRequestResolver authorizeRequestResolver,
    IClientStore clients,
    ILogger<ReturnUrlClientContextResolver> logger) : IReturnUrlClientContextResolver
{
    public async Task<Client?> TryResolveClientAsync(HttpContext http, string? returnUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return null;

        if (!LooksLikeLocalUrl(returnUrl))
            return null;

        if (!TryParseAuthorizeReturnUrl(returnUrl, out var parsedQuery, out var requestUriRaw, out var requestJwt))
            return null;

        // If we don't have an explicit client context, treat this as a non-client-specific sign-in.
        // (E.g., generic "sign in" entry point.)
        var hasClientContext =
            TryGetSingle(parsedQuery, OAuthConstants.Parameters.ClientId) is not null
            || requestUriRaw is not null
            || requestJwt is not null;

        if (!hasClientContext)
            return null;

        var issuer = http.GetIssuer();
        var resolution = await authorizeRequestResolver.ResolveAsync(
            parsedQuery.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)),
            requestUriRaw,
            requestJwt,
            issuer,
            ct).ConfigureAwait(false);

        if (!resolution.IsValid || string.IsNullOrWhiteSpace(resolution.ClientId))
            return null;

        var client = await clients.FindByClientIdAsync(resolution.ClientId, ct).ConfigureAwait(false);
        if (client is null)
        {
            logger.LogDebug("ReturnUrl client resolution produced unknown client_id={ClientId}", resolution.ClientId);
            return null;
        }

        return client;
    }

    private static bool LooksLikeLocalUrl(string url)
    {
        // Strictly allow only app-local paths. Reject absolute URLs and scheme-relative URLs.
        // (This avoids trusting attacker-controlled hosts.)
        if (!url.StartsWith("/", StringComparison.Ordinal))
            return false;

        if (url.StartsWith("//", StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool TryParseAuthorizeReturnUrl(
        string returnUrl,
        out Dictionary<string, string> query,
        out string? requestUriRaw,
        out string? requestJwt)
    {
        query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        requestUriRaw = null;
        requestJwt = null;

        try
        {
            // Use a dummy base to parse a relative URL.
            var uri = new Uri("http://local" + returnUrl);
            var path = uri.AbsolutePath;

            var isAuthorize =
                path.Equals("/authorize", StringComparison.OrdinalIgnoreCase)
                || (path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith("/authorize", StringComparison.OrdinalIgnoreCase));

            if (!isAuthorize)
                return false;

            var parsed = QueryHelpers.ParseQuery(uri.Query);
            foreach (var kv in parsed)
            {
                query[kv.Key] = kv.Value.LastOrDefault() ?? string.Empty;
            }

            requestUriRaw = TryGetSingle(query, OAuthConstants.Parameters.RequestUri);
            requestJwt = TryGetSingle(query, OAuthConstants.Parameters.Request);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetSingle(IReadOnlyDictionary<string, string> query, string key)
    {
        if (!query.TryGetValue(key, out var val))
            return null;
        return string.IsNullOrWhiteSpace(val) ? null : val;
    }
}
