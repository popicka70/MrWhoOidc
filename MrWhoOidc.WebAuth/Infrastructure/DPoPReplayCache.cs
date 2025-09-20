using System.Collections.Concurrent;

namespace MrWhoOidc.WebAuth.Infrastructure;

public interface IDPoPReplayCache
{
    bool TryAdd(string key, DateTimeOffset expiresAt);
}

internal sealed class InMemoryDPoPReplayCache : IDPoPReplayCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _store = new(StringComparer.Ordinal);

    public bool TryAdd(string key, DateTimeOffset expiresAt)
    {
        Cleanup();
        return _store.TryAdd(key, expiresAt);
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _store)
        {
            if (kvp.Value <= now)
            {
                _store.TryRemove(kvp.Key, out _);
            }
        }
    }
}
