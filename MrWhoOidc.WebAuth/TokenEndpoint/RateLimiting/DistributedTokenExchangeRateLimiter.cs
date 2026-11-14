using Microsoft.Extensions.Options;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;

namespace MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting;

/// <summary>
/// Store-backed per-client fixed-window limiter for token-exchange. The store's atomic increment
/// gives the same single-round-trip semantics the previous Redis Lua script provided, whether the
/// backend is the database or Redis.
/// Key pattern: te:rl:{tenantId}:{clientBucket}:{yyyyMMddHHmm} (minute precision UTC).
/// </summary>
public sealed class DistributedTokenExchangeRateLimiter(
    IDistributedStateStore store,
    IOptions<TokenExchangeRateLimitOptions> options) : ITokenExchangeRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public async Task<TokenExchangeRateLimitResult> ShouldAllowAsync(Guid tenantId, string clientId, CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.Enabled || opts.PerClientPerMinute <= 0)
        {
            return new TokenExchangeRateLimitResult(true, null);
        }

        var now = DateTimeOffset.UtcNow;
        var key = BuildKey(tenantId, clientId, now);
        var ttl = Window.Add(TimeSpan.FromSeconds(5));

        var count = await store.IncrementAsync(key, ttl, ct).ConfigureAwait(false);
        if (count > opts.PerClientPerMinute)
        {
            var remaining = await store.GetTimeToLiveAsync(key, ct).ConfigureAwait(false);
            var retry = (int)Math.Ceiling((remaining ?? TimeSpan.FromSeconds(60)).TotalSeconds);
            if (retry < 1) retry = 1;
            return new TokenExchangeRateLimitResult(false, retry);
        }

        return new TokenExchangeRateLimitResult(true, null);
    }

    internal static string BuildKey(Guid tenantId, string clientId, DateTimeOffset now)
        => $"te:rl:{tenantId:N}:{clientId}:{now.UtcDateTime:yyyyMMddHHmm}";
}
