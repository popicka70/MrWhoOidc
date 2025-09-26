using StackExchange.Redis;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting;

/// <summary>
/// Redis-backed per-client fixed window limiter for token-exchange.
/// Uses atomic INCR + TTL to enforce a max count per rolling minute.
/// Key pattern: te:rl:{clientBucket}:{yyyyMMddHHmm} (minute precision UTC)
/// </summary>
public sealed class RedisTokenExchangeRateLimiter : ITokenExchangeRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IOptions<TokenExchangeRateLimitOptions> _options;
    private readonly IDatabase _db;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

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

        // Minute bucket key
        var now = DateTimeOffset.UtcNow;
        var minuteKey = $"te:rl:{clientId}:{now:yyyyMMddHHmm}";
        // Increment and fetch new count atomically
        var count = await _db.StringIncrementAsync(minuteKey);
        if (count == 1)
        {
            // Set expiry only on first creation to one minute + small jitter
            _ = _db.KeyExpireAsync(minuteKey, Window.Add(TimeSpan.FromSeconds(5)));
        }
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
