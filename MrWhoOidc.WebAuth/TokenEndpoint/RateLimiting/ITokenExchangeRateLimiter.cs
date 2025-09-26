using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting;

public sealed record TokenExchangeRateLimitResult(bool Allowed, int? RetryAfterSeconds);

public interface ITokenExchangeRateLimiter
{
    Task<TokenExchangeRateLimitResult> ShouldAllowAsync(string clientId, CancellationToken ct = default);
}

public sealed class TokenExchangeRateLimitOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Requests allowed per client per rolling minute. Default 60 (previous constant).
    /// </summary>
    public int PerClientPerMinute { get; set; } = 60;
}

/// <summary>
/// Simple in-memory per-client minute window limiter (mirrors prior inline TE limiter logic).
/// </summary>
public sealed class InMemoryTokenExchangeRateLimiter : ITokenExchangeRateLimiter
{
    private readonly IOptions<TokenExchangeRateLimitOptions> _options;
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> _windows = new();

    public InMemoryTokenExchangeRateLimiter(IOptions<TokenExchangeRateLimitOptions> options)
    {
        _options = options;
    }

    public Task<TokenExchangeRateLimitResult> ShouldAllowAsync(string clientId, CancellationToken ct = default)
    {
        var opts = _options.Value;
        if (!opts.Enabled || opts.PerClientPerMinute <= 0)
            return Task.FromResult(new TokenExchangeRateLimitResult(true, null));

        var now = DateTimeOffset.UtcNow;
        _windows.AddOrUpdate(clientId, _ => (1, now), (_, cur) =>
        {
            if (now - cur.WindowStart >= TimeSpan.FromMinutes(1))
                return (1, now);
            return (cur.Count + 1, cur.WindowStart);
        });
        var snapshot = _windows[clientId];
        if (snapshot.Count > opts.PerClientPerMinute && now - snapshot.WindowStart < TimeSpan.FromMinutes(1))
        {
            var retry = 60 - (int)(now - snapshot.WindowStart).TotalSeconds;
            if (retry < 1) retry = 1;
            return Task.FromResult(new TokenExchangeRateLimitResult(false, retry));
        }
        return Task.FromResult(new TokenExchangeRateLimitResult(true, null));
    }
}
