using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Infrastructure;

public sealed class RedisJarReplayCache : IJarReplayCache
{
    private readonly IConnectionMultiplexer _mux;
    private readonly IDatabase _db;
    private readonly ILogger<RedisJarReplayCache> _logger;

    public RedisJarReplayCache(IConnectionMultiplexer mux, ILogger<RedisJarReplayCache>? logger = null)
    {
        _mux = mux;
        _db = _mux.GetDatabase();
        _logger = logger ?? NullLogger<RedisJarReplayCache>.Instance;
    }

    public bool TryAdd(string key, DateTimeOffset expiresAt)
    {
        var now = DateTimeOffset.UtcNow;
        var ttl = expiresAt > now ? expiresAt - now : TimeSpan.FromSeconds(1);
        // Use SET NX with expiry to ensure single-writer semantics across instances
        try
        {
            return _db.StringSet(GetKey(key), "1", ttl, When.NotExists);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            // Fail closed: if we cannot record the request object we cannot rule out a replay, so reject it.
            _logger.LogWarning(ex, "Redis unavailable while recording request object jti; rejecting as possible replay.");
            return false;
        }
    }

    private static string GetKey(string key) => $"jar:replay:{key}";
}
