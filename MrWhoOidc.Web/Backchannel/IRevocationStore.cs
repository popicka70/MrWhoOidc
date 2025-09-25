using Microsoft.Extensions.Caching.Distributed;

namespace MrWhoOidc.Web.Backchannel;

public interface IRevocationStore
{
    Task RevokeSidAsync(string sid, TimeSpan ttl, CancellationToken ct = default);
    Task<bool> IsSidRevokedAsync(string sid, CancellationToken ct = default);
}

public sealed class DistributedRevocationStore : IRevocationStore
{
    private readonly IDistributedCache _cache;
    private const string Prefix = "bcl:sid:";
    public DistributedRevocationStore(IDistributedCache cache) => _cache = cache;

    public async Task RevokeSidAsync(string sid, TimeSpan ttl, CancellationToken ct = default)
    {
        await _cache.SetStringAsync(Prefix + sid, "1", new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        }, ct);
    }

    public async Task<bool> IsSidRevokedAsync(string sid, CancellationToken ct = default)
    {
        var val = await _cache.GetStringAsync(Prefix + sid, ct);
        return val is not null;
    }
}

public sealed class MemoryRevocationStore : IRevocationStore
{
    private readonly Dictionary<string, DateTimeOffset> _revoked = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public Task RevokeSidAsync(string sid, TimeSpan ttl, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _revoked[sid] = DateTimeOffset.UtcNow.Add(ttl);
        }
        return Task.CompletedTask;
    }

    public Task<bool> IsSidRevokedAsync(string sid, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_revoked.TryGetValue(sid, out var exp))
            {
                if (exp > DateTimeOffset.UtcNow) return Task.FromResult(true);
                _revoked.Remove(sid);
            }
        }
        return Task.FromResult(false);
    }
}
