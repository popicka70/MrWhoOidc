using System.Diagnostics.CodeAnalysis;
using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace MrWhoOidc.WebAuth.Infrastructure;

public sealed class RedisFixedWindowRateLimiterOptions
{
    public int PermitLimit { get; set; } = 100;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
    public string Prefix { get; set; } = "rl";
}

public sealed class RedisFixedWindowRateLimiter : RateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly string _name;
    private readonly RedisFixedWindowRateLimiterOptions _options;

    public RedisFixedWindowRateLimiter(IConnectionMultiplexer redis, string name, RedisFixedWindowRateLimiterOptions options)
    {
        _redis = redis;
        _db = redis.GetDatabase();
        _name = name;
        _options = options;
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        // Try sync acquire by blocking on async (best effort). Middleware generally uses async path.
        try
        {
            return AcquireAsyncCore(permitCount, default).GetAwaiter().GetResult();
        }
        catch
        {
            return new SimpleLease(true); // fail-open
        }
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        if (permitCount != 1)
        {
            return new SimpleLease(false);
        }

        var windowSeconds = (int)Math.Max(1, _options.Window.TotalSeconds);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bucket = now / windowSeconds;
        var key = $"{_options.Prefix}:{_name}:{bucket}";

        long count;
        try
        {
            count = await _db.StringIncrementAsync(key);
            if (count == 1)
            {
                await _db.KeyExpireAsync(key, TimeSpan.FromSeconds(windowSeconds + 1));
            }
        }
        catch
        {
            // Fail-open on Redis errors
            return new SimpleLease(true);
        }

        var acquired = count <= _options.PermitLimit;
        return new SimpleLease(acquired);
    }

    private sealed class SimpleLease(bool acquired) : RateLimitLease
    {
        public override bool IsAcquired => acquired;
        public override IEnumerable<string> MetadataNames => Array.Empty<string>();
        public override bool TryGetMetadata(string metadataName, [NotNullWhen(true)] out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
