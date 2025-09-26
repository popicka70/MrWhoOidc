using System.Collections.Concurrent;

using MrWhoOidc.Security;
namespace MrWhoOidc.WebAuth.Infrastructure;

internal sealed class InMemoryDPoPReplayCache : IDPoPReplayCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _store = new(StringComparer.Ordinal);

    public bool TryAdd(string key, DateTimeOffset expiresAt)
    {
        var now = DateTimeOffset.UtcNow;
        if (_store.TryGetValue(key, out var existing))
        {
            if (existing > now) return false;
            _store.TryRemove(key, out _);
        }
        return _store.TryAdd(key, expiresAt);
    }
}
