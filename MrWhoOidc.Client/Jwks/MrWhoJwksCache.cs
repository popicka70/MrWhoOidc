using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Client.Discovery;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.Client.Jwks;

internal sealed class MrWhoJwksCache : IMrWhoJwksCache
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMrWhoDiscoveryClient _discoveryClient;
    private readonly IOptionsMonitor<MrWhoOidcClientOptions> _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MrWhoJwksCache> _logger;

    private const string CacheKey = "mrwho:jwks";

    public MrWhoJwksCache(IHttpClientFactory httpClientFactory, IMrWhoDiscoveryClient discoveryClient, IOptionsMonitor<MrWhoOidcClientOptions> options, IMemoryCache cache, ILogger<MrWhoJwksCache> logger)
    {
        _httpClientFactory = httpClientFactory;
        _discoveryClient = discoveryClient;
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public async ValueTask<JsonWebKeySet> GetAsync(CancellationToken cancellationToken = default)
    {
        var opts = _options.CurrentValue;
        var cacheKey = CacheKey + ":" + opts.Name;

        if (_cache.TryGetValue<(JsonWebKeySet Keys, DateTimeOffset Expires)>(cacheKey, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
        {
            return entry.Keys;
        }

        var discovery = await _discoveryClient.GetAsync(cancellationToken).ConfigureAwait(false);
        var jwksUri = discovery.RequireHttps(discovery.JwksUri, _options.CurrentValue.RequireHttpsMetadata);

        var client = _httpClientFactory.CreateClient(opts.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, jwksUri);
        using var activity = MrWhoOidcClientDefaults.ActivitySource.StartActivity("JWKS.Fetch");
        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var jwks = await response.Content.ReadFromJsonAsync<JsonWebKeySet>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Failed to deserialize JWKS document.");

        var ttl = opts.MetadataRefreshInterval;
        _cache.Set(cacheKey, (jwks, DateTimeOffset.UtcNow.Add(ttl)));
        _logger.LogInformation("JWKS cache refreshed with {KeyCount} keys", jwks.Keys.Count);
        return jwks;
    }

    public void Invalidate()
    {
        var cacheKey = CacheKey + ":" + _options.CurrentValue.Name;
        _cache.Remove(cacheKey);
    }
}
