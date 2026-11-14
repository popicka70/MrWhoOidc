using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;

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
    /// DI factory: the configured distributed store (database by default, Redis when selected).
    /// The counter increment is atomic in both backends, so parallel guesses cannot overwrite
    /// each other's count across replicas.
    /// </summary>
    public static ILoginRateLimiter Create(IServiceProvider services)
        => new DistributedLoginRateLimiter(
            services.GetRequiredService<IDistributedStateStore>(),
            services.GetRequiredService<ITenantAccessor>());

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
/// Store-backed limiter: each failure is a single atomic increment (the store sets the window
/// expiry on the first one), so parallel guesses cannot overwrite each other's count.
/// </summary>
public sealed class DistributedLoginRateLimiter(
    IDistributedStateStore store,
    ITenantAccessor tenantAccessor) : ILoginRateLimiter
{
    public async Task<bool> IsLockedOutAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default)
    {
        var value = await store.GetAsync(Key(httpContext, username), cancellationToken).ConfigureAwait(false);
        return long.TryParse(value, out var attempts) && attempts >= LoginRateLimit.MaxAttempts;
    }

    public Task RegisterFailedAttemptAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default)
        => store.IncrementAsync(Key(httpContext, username), LoginRateLimit.Window, cancellationToken);

    public Task ClearAsync(HttpContext httpContext, string username, CancellationToken cancellationToken = default)
        => store.RemoveAsync(Key(httpContext, username), cancellationToken);

    internal string Key(HttpContext httpContext, string username)
        => $"login-rl:{LoginRateLimit.KeyHash(httpContext, tenantAccessor, username)}";
}
