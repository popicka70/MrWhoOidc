using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using MrWhoOidc.Security;

namespace MrWhoOidc.WebAuth.Infrastructure;

internal sealed class RedisDPoPReplayCache : MrWhoOidc.Security.IDPoPReplayCache
{
    private readonly IConnectionMultiplexer _mux;
    private readonly IDatabase _db;
    private readonly ILogger<RedisDPoPReplayCache> _logger;

    public RedisDPoPReplayCache(IConnectionMultiplexer mux, ILogger<RedisDPoPReplayCache>? logger = null)
    {
        _mux = mux;
        _db = _mux.GetDatabase();
        _logger = logger ?? NullLogger<RedisDPoPReplayCache>.Instance;
    }

    public bool TryAdd(string key, DateTimeOffset expiresAt)
    {
        var ttl = expiresAt > DateTimeOffset.UtcNow ? expiresAt - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1);
        // Use SET NX with expiry
        try
        {
            return _db.StringSet(GetKey(key), "1", ttl, When.NotExists);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            // Fail closed: if we cannot record the DPoP proof we cannot rule out a replay, so reject it.
            _logger.LogWarning(ex, "Redis unavailable while recording DPoP proof jti; rejecting as possible replay.");
            return false;
        }
    }

    private static string GetKey(string key) => $"dpop:replay:{key}";
}
