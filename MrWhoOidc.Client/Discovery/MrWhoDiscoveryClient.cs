using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.Client.Discovery;

internal sealed class MrWhoDiscoveryClient : IMrWhoDiscoveryClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<MrWhoOidcClientOptions> _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MrWhoDiscoveryClient> _logger;

    private const string CacheKey = "mrwho:discovery";

    public MrWhoDiscoveryClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<MrWhoOidcClientOptions> options, IMemoryCache cache, ILogger<MrWhoDiscoveryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public async ValueTask<MrWhoDiscoveryDocument> GetAsync(CancellationToken cancellationToken = default)
    {
        var opts = _options.CurrentValue;
        var cacheKey = CacheKey + ":" + opts.Name;

        if (_cache.TryGetValue<(MrWhoDiscoveryDocument Document, string? Etag, DateTimeOffset Expires)>(cacheKey, out var entry))
        {
            if (entry.Expires > DateTimeOffset.UtcNow)
            {
                return entry.Document;
            }
        }

        var httpClient = _httpClientFactory.CreateClient(opts.HttpClientName);
        var issuerUri = new Uri(opts.Issuer!, UriKind.Absolute);
        var discoveryUri = opts.DiscoveryUri ?? new Uri(issuerUri, ".well-known/openid-configuration");

        using var request = new HttpRequestMessage(HttpMethod.Get, discoveryUri);
        if (entry.Etag is not null)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(entry.Etag));
        }

        using var activity = MrWhoOidcClientDefaults.ActivitySource.StartActivity("Discovery.Fetch");
        var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotModified && entry.Document is not null)
        {
            _logger.LogDebug("Discovery document not modified; reusing cached version.");
            _cache.Set(cacheKey, (entry.Document, entry.Etag, DateTimeOffset.UtcNow.Add(opts.MetadataRefreshInterval)));
            return entry.Document;
        }

        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<MrWhoDiscoveryDocument>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Failed to deserialize discovery document.");

        var etag = response.Headers.ETag?.Tag;
        _cache.Set(cacheKey, (document, etag, DateTimeOffset.UtcNow.Add(opts.MetadataRefreshInterval)));
        _logger.LogInformation("Discovery document refreshed; token endpoint {TokenEndpoint}", document.TokenEndpoint);
        return document;
    }
}
