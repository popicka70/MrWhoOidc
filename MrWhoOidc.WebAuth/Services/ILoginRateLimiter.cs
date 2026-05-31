using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Services;

public interface ILoginRateLimiter
{
    Task<bool> IsLockedOutAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default);
    Task RegisterFailedAttemptAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default);
    Task ClearAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default);
}

public sealed class DistributedLoginRateLimiter(IDistributedCache cache, ITenantAccessor tenantAccessor) : ILoginRateLimiter
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public async Task<bool> IsLockedOutAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(BuildKey(httpContext, username), cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - state.FirstAttemptUtc > Window)
        {
            await ClearAsync(httpContext, username, cancellationToken).ConfigureAwait(false);
            return false;
        }

        return state.Attempts >= MaxAttempts;
    }

    public async Task RegisterFailedAttemptAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(httpContext, username);
        var now = DateTimeOffset.UtcNow;
        var state = await ReadStateAsync(key, cancellationToken).ConfigureAwait(false);
        if (state is null || now - state.FirstAttemptUtc > Window)
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
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Window },
            cancellationToken).ConfigureAwait(false);
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
    {
        var tenantId = tenantAccessor.CurrentTenant?.TenantId.ToString("N") ?? "no-tenant";
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
        var normalizedUsername = username.Trim().ToUpperInvariant();
        var material = $"{tenantId}|{ipAddress}|{normalizedUsername}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"login-rate-limit:{hash}";
    }

    private sealed record LoginRateLimitState(int Attempts, DateTimeOffset FirstAttemptUtc);
}
