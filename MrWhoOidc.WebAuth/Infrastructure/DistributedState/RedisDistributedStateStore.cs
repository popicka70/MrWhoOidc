using StackExchange.Redis;

namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>
/// Redis-backed <see cref="IDistributedStateStore"/>. This is the only Redis-specific type in the
/// host: replay caches, nonces, rate limiters, correlation handles and the distributed-cache
/// adapter all consume the store interface instead.
/// </summary>
public sealed class RedisDistributedStateStore(IConnectionMultiplexer multiplexer) : IDistributedStateStore
{
    // INCR + conditional PEXPIRE in one script. The PTTL guard re-arms the expiry should a key
    // ever be left without one (for example after a failover that lost the EXPIRE).
    private const string IncrementScript = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 or redis.call('PTTL', KEYS[1]) < 0 then
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        return current
        """;

    public async Task<bool> TryAddAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        return await multiplexer.GetDatabase()
            .StringSetAsync(key, value, NormalizeTtl(ttl), When.NotExists)
            .ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        var value = await multiplexer.GetDatabase().StringGetAsync(key).ConfigureAwait(false);
        return value.HasValue ? value.ToString() : null;
    }

    public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        await multiplexer.GetDatabase()
            .StringSetAsync(key, value, NormalizeTtl(ttl), When.Always)
            .ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        return await multiplexer.GetDatabase().KeyDeleteAsync(key).ConfigureAwait(false);
    }

    public async Task<bool> RefreshAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        return await multiplexer.GetDatabase().KeyExpireAsync(key, NormalizeTtl(ttl)).ConfigureAwait(false);
    }

    public async Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        var ttlMilliseconds = (long)NormalizeTtl(ttl).TotalMilliseconds;
        var result = await multiplexer.GetDatabase()
            .ScriptEvaluateAsync(IncrementScript, keys: [key], values: [ttlMilliseconds])
            .ConfigureAwait(false);
        return (long)result;
    }

    public async Task<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        return await multiplexer.GetDatabase().KeyTimeToLiveAsync(key).ConfigureAwait(false);
    }

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken ct = default)
    {
        // Redis expires keys natively.
        return Task.FromResult(0);
    }

    private static TimeSpan NormalizeTtl(TimeSpan ttl)
        => ttl <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : ttl;
}
