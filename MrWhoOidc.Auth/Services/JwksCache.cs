using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.Auth.Services;

public interface IJwksCache
{
    Task<JsonWebKeySet?> GetAsync(string jwksUri, TimeSpan ttl, IHttpClientFactory httpFactory, CancellationToken ct = default);
}

public sealed class JwksCache : IJwksCache
{
    private sealed record Entry(JsonWebKeySet Set, DateTimeOffset ExpiresAt);
    private static readonly ConcurrentDictionary<string, Entry> _cache = new();

    public async Task<JsonWebKeySet?> GetAsync(string jwksUri, TimeSpan ttl, IHttpClientFactory httpFactory, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(jwksUri, out var e) && e.ExpiresAt > now)
            return e.Set;

        try
        {
            var http = httpFactory.CreateClient();
            using var resp = await http.GetAsync(jwksUri, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var set = new JsonWebKeySet(json);
            _cache[jwksUri] = new Entry(set, now.Add(ttl));
            return set;
        }
        catch
        {
            // On failure, keep stale if present
            if (e is not null) return e.Set;
            return null;
        }
    }
}
