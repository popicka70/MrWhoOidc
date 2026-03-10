using StackExchange.Redis;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting;

/// <summary>
/// Redis-backed per-client fixed window limiter for token-exchange.
/// Uses an atomic Lua script (INCR + EXPIRE in one round-trip) to avoid the
/// race condition where a crash between INCR and EXPIRE would leave an immortal key.
/// Key pattern: te:rl:{clientBucket}:{yyyyMMddHHmm} (minute precision UTC)
/// </summary>
public sealed class RedisTokenExchangeRateLimiter : ITokenExchangeRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IOptions<TokenExchangeRateLimitOptions> _options;
    private readonly IDatabase _db;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // Lua script: atomically increments the counter and sets a TTL on first creation.
    // Returns the new counter value. The TTL is only set when the key is brand-new
    // (INCR returns 1), preventing a race between INCR and a separate EXPIRE call.
    private static readonly string IncrWithTtlScript = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        return current
        """;

    public RedisTokenExchangeRateLimiter(IConnectionMultiplexer redis, IOptions<TokenExchangeRateLimitOptions> options)
    {
        _redis = redis;
        _options = options;
        _db = redis.GetDatabase();
    }

    public async Task<TokenExchangeRateLimitResult> ShouldAllowAsync(string clientId, CancellationToken ct = default)
    {
        var opts = _options.Value;
        if (!opts.Enabled || opts.PerClientPerMinute <= 0)
            return new TokenExchangeRateLimitResult(true, null);

        var now = DateTimeOffset.UtcNow;
        var minuteKey = $"te:rl:{clientId}:{now:yyyyMMddHHmm}";

        // Atomic INCR + conditional EXPIRE via Lua (single round-trip, no TOCTOU).
        var ttlSeconds = (long)Window.Add(TimeSpan.FromSeconds(5)).TotalSeconds;
        var count = (long)await _db.ScriptEvaluateAsync(
            IncrWithTtlScript,
            keys: [(RedisKey)minuteKey],
            values: [(RedisValue)ttlSeconds]);

        if (count > opts.PerClientPerMinute)
        {
            var ttl = await _db.KeyTimeToLiveAsync(minuteKey) ?? TimeSpan.FromSeconds(60);
            var retry = (int)Math.Ceiling(ttl.TotalSeconds);
            if (retry < 1) retry = 1;
            return new TokenExchangeRateLimitResult(false, retry);
        }
        return new TokenExchangeRateLimitResult(true, null);
    }
}
