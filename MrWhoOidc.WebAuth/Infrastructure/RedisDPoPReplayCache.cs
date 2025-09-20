using StackExchange.Redis;

namespace MrWhoOidc.WebAuth.Infrastructure;

internal sealed class RedisDPoPReplayCache : IDPoPReplayCache
{
    private readonly IConnectionMultiplexer _mux;
    private readonly IDatabase _db;

    public RedisDPoPReplayCache(IConnectionMultiplexer mux)
    {
        _mux = mux;
        _db = _mux.GetDatabase();
    }

    public bool TryAdd(string key, DateTimeOffset expiresAt)
    {
        var ttl = expiresAt > DateTimeOffset.UtcNow ? expiresAt - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1);
        // Use SET NX with expiry
        return _db.StringSet(GetKey(key), "1", ttl, When.NotExists);
    }

    private static string GetKey(string key) => $"dpop:replay:{key}";
}
