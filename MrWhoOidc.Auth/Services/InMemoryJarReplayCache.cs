using System.Collections.Concurrent;

namespace MrWhoOidc.Auth.Services;

internal sealed class InMemoryJarReplayCache : IJarReplayCache
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Store = new(StringComparer.Ordinal);

    public ValueTask<bool> TryAddAsync(string key, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        Cleanup();
        if (Store.TryAdd(key, expiresAt)) return ValueTask.FromResult(true);
        if (Store.TryGetValue(key, out var existing))
        {
            if (existing <= DateTimeOffset.UtcNow)
            {
                Store.TryRemove(key, out _);
                return ValueTask.FromResult(Store.TryAdd(key, expiresAt));
            }
            return ValueTask.FromResult(false); // replay within TTL
        }
        // race: not present anymore, try again
        return ValueTask.FromResult(Store.TryAdd(key, expiresAt));
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
