using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Infrastructure;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Registers all named rate limiting policies used by protocol & admin endpoints.
/// Mirrors the prior inline Program.cs configuration without behavioral changes.
/// </summary>
public static class RateLimitingExtensions
{
    public static IServiceCollection AddRateLimitingPolicies(this IServiceCollection services, bool enableGlobalLimiter)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            if (enableGlobalLimiter)
            {
                // Same token bucket used previously when Redis present (shared fairly per IP).
                // Static web assets (wwwroot CSS/JS/fonts/icons, the app-bundled stylesheet, the
                // favicon) are NOT part of the abuse surface this bucket protects, but every page
                // navigation spends several requests on them. Under parallel real load (three
                // browser sessions on one instance) the per-IP bucket was exhausted and a page
                // 429'd a *font* file — breaking the UI (missing icons) instead of throttling an
                // abuser. Static assets are therefore exempt from the global limiter; protocol
                // and admin endpoints keep their named policies on top of it.
                var limiterOptions = new { PermitLimit = 1000, Window = TimeSpan.FromMinutes(1) };
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                {
                    var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                    var path = httpContext.Request.Path.Value ?? string.Empty;
                    if (IsStaticWebAsset(path))
                    {
                        return RateLimitPartition.GetNoLimiter(key);
                    }

                    return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = limiterOptions.PermitLimit,
                        QueueLimit = 0,
                        TokensPerPeriod = limiterOptions.PermitLimit,
                        ReplenishmentPeriod = limiterOptions.Window,
                        AutoReplenishment = true
                    });
                });
            }

            // authorize endpoint
            options.AddPolicy("rl-authorize", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
            // /token has no ASP.NET Core policy: it is limited by DistributedRateLimiterMiddleware
            // (see EndpointMappingExtensions), so the former rl-token/rl-token-exchange policies were dead.
            options.AddPolicy("rl-userinfo", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
            options.AddPolicy("rl-par", httpContext =>
            {
                // Per-instance limit keyed by tenant + client_id + IP. The cross-instance (Redis) limit for /par is
                // enforced asynchronously by DistributedRateLimiterMiddleware, which also stashes the form client_id
                // so this synchronous partition never has to read the request body.
                var key = RateLimitPartitionKeys.ForClient(httpContext, RateLimitPartitionKeys.GetClientId(httpContext));
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
            options.AddPolicy("rl-introspect", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
            options.AddPolicy("rl-jwks", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 300,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
            // The check_session state endpoint (F1): the relying party polls it, so it needs its own budget.
            // Sharing the authorize budget would let the polling throttle the sign-in itself.
            options.AddPolicy("rl-checksession", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 600,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
            options.AddPolicy("rl-admin", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 200,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            // M-Boot: the bootstrap endpoints are a one-time provisioning capability guarded by a secret.
            // They get their own, much tighter budget instead of relying on the global per-IP limiter.
            options.AddPolicy("rl-bootstrap", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            // QR login rate limiting policies
            options.AddPolicy("rl-qr-poll", httpContext =>
            {
                // Partition by session token to prevent cross-session abuse
                var sessionToken = httpContext.Request.RouteValues["sessionToken"]?.ToString() ?? "unknown";
                return RateLimitPartition.GetSlidingWindowLimiter(sessionToken, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            options.AddPolicy("rl-qr-confirm", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            options.AddPolicy("rl-qr-cancel", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            // Tenant discovery rate limiting policy
            options.AddPolicy("email-discovery", httpContext =>
            {
                if (HttpMethods.IsGet(httpContext.Request.Method) || HttpMethods.IsHead(httpContext.Request.Method))
                {
                    return RateLimitPartition.GetNoLimiter("email-discovery-page");
                }

                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            // logout endpoints
            options.AddPolicy("rl-logout", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            // revocation endpoint
            options.AddPolicy("rl-revoke", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            // external OIDC endpoints
            options.AddPolicy("rl-external", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });
        return services;
    }

    /// <summary>
    /// Paths that must never consume the global per-IP rate budget. These are immutable,
    /// cacheable files served from wwwroot (or the Razor-compiled stylesheet bundle) — they
    /// carry no protocol semantics and are the payload of a page render, not an attack
    /// vector. Cacheable statics are the standard exemption for coarse rate limiters;
    /// throttling them breaks legitimate page loads under parallel sessions.
    /// </summary>
    internal static bool IsStaticWebAsset(string path)
    {
        return path.StartsWith("/css", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/js", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/lib", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/images", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/MrWhoOidc.WebAuth.styles.css", StringComparison.OrdinalIgnoreCase);
    }
}
