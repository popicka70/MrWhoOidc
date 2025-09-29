using System;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MrWhoOidc.WebAuth.Observability;

public interface ICorrelationStateCache
{
    Task<string> StoreAsync(string correlationId, CancellationToken cancellationToken);
    Task<string?> TryGetAsync(string handle, bool consume, CancellationToken cancellationToken);
}

internal sealed class CorrelationStateCache : ICorrelationStateCache
{
    private readonly IMemoryCache _memoryCache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<CorrelationStateCache> _logger;
    private readonly IOidcMetrics _metrics;
    private readonly ICorrelationIdGenerator _generator;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public CorrelationStateCache(IMemoryCache memoryCache, IConnectionMultiplexer? redis, ILogger<CorrelationStateCache> logger, IOidcMetrics metrics, ICorrelationIdGenerator generator)
    {
        _memoryCache = memoryCache;
        _redis = redis;
        _logger = logger;
        _metrics = metrics;
        _generator = generator;
    }

    public async Task<string> StoreAsync(string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) throw new ArgumentException("Correlation ID required", nameof(correlationId));
        var handle = _generator.GenerateHandle();
        SetMemory(handle, correlationId);
        if (_redis is not null)
        {
            try
            {
                var db = _redis.GetDatabase();
                await db.StringSetAsync(RedisKey(handle), correlationId, Ttl, When.Always);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write correlation handle to Redis");
            }
        }
        _metrics.CorrelationCacheWrites.Add(1);
        return handle;
    }

    public async Task<string?> TryGetAsync(string handle, bool consume, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(handle)) return null;
        if (_memoryCache.TryGetValue<Entry>(MemoryKey(handle), out var entry))
        {
            if (entry.Expiry <= DateTimeOffset.UtcNow)
            {
                _memoryCache.Remove(MemoryKey(handle));
                _metrics.CorrelationCacheStale.Add(1);
            }
            else
            {
                if (consume)
                {
                    _memoryCache.Remove(MemoryKey(handle));
                }
                else
                {
                    SetMemory(handle, entry.CorrelationId); // refresh TTL
                }
                _metrics.CorrelationCacheHits.Add(1);
                return entry.CorrelationId;
            }
        }

        if (_redis is not null)
        {
            try
            {
                var db = _redis.GetDatabase();
                var redisKey = RedisKey(handle);
                var value = await db.StringGetAsync(redisKey);
                if (value.HasValue)
                {
                    var cid = value.ToString();
                    if (consume)
                    {
                        _ = db.KeyDeleteAsync(redisKey);
                        _memoryCache.Remove(MemoryKey(handle));
                    }
                    else
                    {
                        await db.KeyExpireAsync(redisKey, Ttl);
                        SetMemory(handle, cid);
                    }
                    _metrics.CorrelationCacheHits.Add(1);
                    return cid;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read correlation handle from Redis");
            }
        }

        _metrics.CorrelationCacheMisses.Add(1);
        return null;
    }

    private void SetMemory(string handle, string correlationId)
    {
        var entry = new Entry(correlationId, DateTimeOffset.UtcNow.Add(Ttl));
        _memoryCache.Set(MemoryKey(handle), entry, entry.Expiry);
    }

    private static string MemoryKey(string handle) => "cid:handle:" + handle;
    private static string RedisKey(string handle) => "cid:handle:" + handle;

    private readonly record struct Entry(string CorrelationId, DateTimeOffset Expiry);
}
