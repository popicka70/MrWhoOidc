using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Fetches signed request objects from a client's pre-registered <c>request_uri</c> values
/// (OIDC Core 6.2 / RFC 9101 §5.5). The fetch is SSRF-safe: no redirects, no proxy, internal
/// addresses rejected, response capped.
/// </summary>
public sealed class RequestObjectUriFetcher
{
    public const string SafeHttpClientName = "request-object-safe";

    /// <summary>A request object is a compact JWS/JWE; 50 KiB is far beyond any legitimate one.</summary>
    public const int MaxRequestUriObjectBytes = 50 * 1024;

    /// <summary>OIDC Core 6.2.1: "The entire Request URI MUST NOT exceed 512 bytes".</summary>
    public const int MaxRequestUriBytes = 512;

    private readonly AuthDbContext _db;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpMessageHandler? _messageHandler;
    private readonly ILogger? _logger;

    public RequestObjectUriFetcher(AuthDbContext db, IHttpClientFactory? httpClientFactory = null, HttpMessageHandler? messageHandler = null, ILogger<RequestObjectUriFetcher>? logger = null)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _messageHandler = messageHandler;
        _logger = logger;
    }

    /// <summary>Result of an external request_uri resolution attempt.</summary>
    public sealed record RequestObjectUriResult(
        bool Success,
        string? Error,
        string? ErrorDescription,
        string? RequestJwt,
        string? ClientId);

    /// <summary>
    /// Validates the <c>request_uri</c> against the client's pre-registered <c>request_uris</c> and fetches the
    /// signed request object it references. The <c>request_uri</c> MAY carry a fragment (OIDC Core 6.2.1 hash
    /// fragment); it is stripped before fetching and the registration comparison ignores fragments on both sides.
    /// </summary>
    public async Task<RequestObjectUriResult> TryFetchAsync(
        string requestUriRaw,
        string? clientId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestUriRaw))
        {
            return Failed("invalid_request", "request_uri is required");
        }

        if (Encoding.UTF8.GetByteCount(requestUriRaw) > MaxRequestUriBytes)
        {
            return Failed("invalid_request", "request_uri exceeds 512 bytes");
        }

        if (!Uri.TryCreate(requestUriRaw, UriKind.Absolute, out var requestUri))
        {
            return Failed("invalid_request", $"Invalid request_uri: {requestUriRaw}");
        }

        // The OP fetches the request object itself, so the URI must be https (http only for loopback
        // development hosts), mirroring the jwks_uri fetch policy.
        if (!(string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
              || (string.Equals(requestUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && requestUri.IsLoopback)))
        {
            return Failed("invalid_request", $"request_uri scheme '{requestUri.Scheme}' is not allowed");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Failed("invalid_request", "client_id is required with request_uri");
        }

        var client = await _db.Clients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct)
            .ConfigureAwait(false);

        if (client is null)
        {
            return Failed("invalid_request", "Unknown client_id");
        }

        var registered = ParseRequestUris(client.RequestUrisJson);
        if (registered.Count == 0 || !IsRegistered(StripFragment(requestUriRaw), registered))
        {
            _logger?.LogWarning("request_uri {RequestUri} is not pre-registered for client {ClientId}", requestUriRaw, clientId);
            return Failed("invalid_request", "request_uri is not pre-registered for this client");
        }

        string jwt;
        try
        {
            var http = CreateHttpClient();
            // OIDC Core 6.2.1: the fragment is a hash of the referenced contents; it is not part of the fetch URL.
            // HttpRequestUrl is scheme + authority + path + query (fragment excluded).
            var fetchUri = new Uri(requestUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped));
            using var response = await http.GetAsync(fetchUri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("request_uri {RequestUri} returned {Status}", fetchUri, (int)response.StatusCode);
                return Failed("invalid_request_object", $"request_uri returned {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength is > MaxRequestUriObjectBytes)
            {
                return Failed("invalid_request_object", "request_uri response exceeds the size limit");
            }

            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(raw) > MaxRequestUriObjectBytes)
            {
                return Failed("invalid_request_object", "request_uri response exceeds the size limit");
            }

            jwt = raw.Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            _logger?.LogWarning(ex, "Failed to fetch the request object from {RequestUri}", requestUriRaw);
            return Failed("invalid_request_object", "Unable to fetch the request object");
        }

        if (string.IsNullOrWhiteSpace(jwt) || !LooksLikeJwt(jwt))
        {
            return Failed("invalid_request_object", "request_uri did not return a request object JWT");
        }

        return new RequestObjectUriResult(true, null, null, jwt, clientId);
    }

    private HttpClient CreateHttpClient()
    {
        if (_messageHandler is not null)
        {
            // Test path: a stubbed handler, no network.
            return new HttpClient(_messageHandler, disposeHandler: false);
        }

        if (_httpClientFactory is not null)
        {
            return _httpClientFactory.CreateClient(SafeHttpClientName);
        }

        // The safe handler blocks internal addresses, redirects and proxies by itself.
        return MrWhoOidc.Auth.Utils.NetworkSecurity.CreateSafeHttpClient(TimeSpan.FromSeconds(10));
    }

    private static bool LooksLikeJwt(string value)
    {
        // A compact JWS has 3 dot-separated segments; a nested JWE has 5. (4 is an unusual flat JWE.)
        var segments = value.Split('.');
        return segments.Length is 3 or 4 or 5;
    }

    private static string StripFragment(string uri)
    {
        var hash = uri.IndexOf('#');
        return hash >= 0 ? uri[..hash] : uri;
    }

    private static IReadOnlyList<string> ParseRequestUris(string? requestUrisJson)
    {
        if (string.IsNullOrWhiteSpace(requestUrisJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(requestUrisJson) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static bool IsRegistered(string requestUriWithoutFragment, IEnumerable<string> registered)
    {
        foreach (var candidate in registered)
        {
            if (string.Equals(StripFragment(candidate), requestUriWithoutFragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static RequestObjectUriResult Failed(string error, string description)
        => new(false, error, description, null, null);
}
