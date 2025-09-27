using StackExchange.Redis;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Infrastructure;

public sealed class RedisJarReplayCache : IJarReplayCache
{
    private readonly IConnectionMultiplexer _mux;
    private readonly IDatabase _db;

    public RedisJarReplayCache(IConnectionMultiplexer mux)
    {
        _mux = mux;
        _db = _mux.GetDatabase();
    }

    public bool TryAdd(string key, DateTimeOffset expiresAt)
    {
        var now = DateTimeOffset.UtcNow;
        var ttl = expiresAt > now ? expiresAt - now : TimeSpan.FromSeconds(1);
        // Use SET NX with expiry to ensure single-writer semantics across instances
        return _db.StringSet(GetKey(key), "1", ttl, When.NotExists);
    }

    private static string GetKey(string key) => $"jar:replay:{key}";
}
