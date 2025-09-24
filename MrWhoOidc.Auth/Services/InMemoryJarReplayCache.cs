using System.Collections.Concurrent;

namespace MrWhoOidc.Auth.Services;

internal sealed class InMemoryJarReplayCache : IJarReplayCache
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Store = new(StringComparer.Ordinal);

    public bool TryAdd(string key, DateTimeOffset expiresAt)
    {
        Cleanup();
        if (Store.TryAdd(key, expiresAt)) return true;
        if (Store.TryGetValue(key, out var existing))
        {
            if (existing <= DateTimeOffset.UtcNow)
            {
                Store.TryRemove(key, out _);
                return Store.TryAdd(key, expiresAt);
            }
            return false; // replay within TTL
        }
        // race: not present anymore, try again
        return Store.TryAdd(key, expiresAt);
    }

    private static void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in Store)
        {
            if (kv.Value <= now)
            {
                Store.TryRemove(kv.Key, out _);
            }
        }
    }
}
