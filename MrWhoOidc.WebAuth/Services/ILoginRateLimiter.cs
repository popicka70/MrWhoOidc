using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using StackExchange.Redis;

namespace MrWhoOidc.WebAuth.Services;

public interface ILoginRateLimiter
{
    Task<bool> IsLockedOutAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default);
    Task RegisterFailedAttemptAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default);
    Task ClearAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default);
}

/// <summary>Limits and key shape shared by the login rate limiter implementations.</summary>
public static class LoginRateLimit
{
    /// <summary>
    /// DI factory: Redis when configured (atomic INCR shared by all nodes), otherwise the in-process cache fallback.
    /// </summary>
    public static ILoginRateLimiter Create(IServiceProvider services)
        => services.GetService<IConnectionMultiplexer>() is { } redis
            ? new RedisLoginRateLimiter(redis, services.GetRequiredService<ITenantAccessor>())
            : new DistributedLoginRateLimiter(services.GetRequiredService<IDistributedCache>(), services.GetRequiredService<ITenantAccessor>());

    internal const int MaxAttempts = 5;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>Per tenant + client IP + username; hashed so neither the IP nor the username is stored.</summary>
    internal static string KeyHash(HttpContext httpContext, ITenantAccessor tenantAccessor, string username)
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId.ToString("N") ?? "no-tenant";
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
        var normalizedUsername = username.Trim().ToUpperInvariant();
        var material = $"{tenantId}|{ipAddress}|{normalizedUsername}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}

/// <summary>
/// Redis-backed limiter: each failure is a single atomic INCR (with the window's expiry set on the first one), so
/// parallel guesses cannot overwrite each other's count the way a read-modify-write of a cached blob could.
/// </summary>
public sealed class RedisLoginRateLimiter(IConnectionMultiplexer redis, ITenantAccessor tenantAccessor) : ILoginRateLimiter
{
    // Fixed window from the first failure. The PTTL guard re-arms the expiry should a key ever be left without one.
    private const string IncrementScript = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 or redis.call('PTTL', KEYS[1]) < 0 then
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        return current
        """;

    public async Task<bool> IsLockedOutAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default)
    {
        var value = await redis.GetDatabase().StringGetAsync(Key(httpContext, username)).ConfigureAwait(false);
        return value.TryParse(out long attempts) && attempts >= LoginRateLimit.MaxAttempts;
    }

    public Task RegisterFailedAttemptAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default)
        => redis.GetDatabase().ScriptEvaluateAsync(
            IncrementScript,
            keys: [Key(httpContext, username)],
            values: [(long)LoginRateLimit.Window.TotalMilliseconds]);

    public Task ClearAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default)
        => redis.GetDatabase().KeyDeleteAsync(Key(httpContext, username));

    // Distinct from the IDistributedCache entries (Redis hashes) the previous implementation wrote, so a rolling
    // deploy never runs INCR against a hash.
    internal RedisKey Key(HttpContext httpContext, string username)
        => $"mrwhooidc:login-rl:{LoginRateLimit.KeyHash(httpContext, tenantAccessor, username)}";
}

/// <summary>
/// Fallback when Redis is not configured, where <see cref="IDistributedCache"/> is the in-process memory cache.
/// The read-modify-write of the counter is serialised per key within the process, which is all the memory cache
/// is shared across.
/// </summary>
public sealed class DistributedLoginRateLimiter(IDistributedCache cache, ITenantAccessor tenantAccessor) : ILoginRateLimiter
{
    private static readonly SemaphoreSlim[] Stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<bool> IsLockedOutAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(BuildKey(httpContext, username), cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - state.FirstAttemptUtc > LoginRateLimit.Window)
        {
            await ClearAsync(httpContext, username, cancellationToken).ConfigureAwait(false);
            return false;
        }

        return state.Attempts >= LoginRateLimit.MaxAttempts;
    }

    public async Task RegisterFailedAttemptAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(httpContext, username);
        var stripe = Stripes[(StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % Stripes.Length];
        await stripe.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var state = await ReadStateAsync(key, cancellationToken).ConfigureAwait(false);
            if (state is null || now - state.FirstAttemptUtc > LoginRateLimit.Window)
            {
                state = new LoginRateLimitState(1, now);
            }
            else
            {
                state = state with { Attempts = state.Attempts + 1 };
            }

            await cache.SetStringAsync(
                key,
                JsonSerializer.Serialize(state),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = LoginRateLimit.Window },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stripe.Release();
        }
    }

    public Task ClearAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default)
        => cache.RemoveAsync(BuildKey(httpContext, username), cancellationToken);

    private async Task<LoginRateLimitState?> ReadStateAsync(string key, CancellationToken cancellationToken)
    {
        var json = await cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LoginRateLimitState>(json);
        }
        catch (JsonException)
        {
            await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private string BuildKey(HttpContext httpContext, string username)
        => $"login-rate-limit:{LoginRateLimit.KeyHash(httpContext, tenantAccessor, username)}";

    private sealed record LoginRateLimitState(int Attempts, DateTimeOffset FirstAttemptUtc);
}
