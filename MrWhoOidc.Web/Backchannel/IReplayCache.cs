using Microsoft.Extensions.Caching.Distributed;

namespace MrWhoOidc.Web.Backchannel;

public interface IReplayCache
{
    Task<bool> TryStoreAsync(string jti, TimeSpan ttl, CancellationToken ct = default);
}

public sealed class DistributedReplayCache : IReplayCache
{
    private readonly IDistributedCache _cache;
    private const string Prefix = "bcl:jti:";
    public DistributedReplayCache(IDistributedCache cache) => _cache = cache;

    public async Task<bool> TryStoreAsync(string jti, TimeSpan ttl, CancellationToken ct = default)
    {
        var key = Prefix + jti;
        // No atomic add in IDistributedCache. Use Get then Set as best-effort; Redis impl will overwrite.
        if (await _cache.GetStringAsync(key, ct) is not null) return false;
        await _cache.SetStringAsync(key, "1", new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        }, ct);
        return true;
    }
}

public sealed class MemoryReplayCache : IReplayCache
{
    private readonly Dictionary<string, DateTimeOffset> _jtis = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public Task<bool> TryStoreAsync(string jti, TimeSpan ttl, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_jtis.TryGetValue(jti, out var exp) && exp > DateTimeOffset.UtcNow)
                return Task.FromResult(false);
            _jtis[jti] = DateTimeOffset.UtcNow.Add(ttl);
        }
        return Task.FromResult(true);
    }
}
