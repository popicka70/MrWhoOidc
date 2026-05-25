using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Infrastructure;
using StackExchange.Redis;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Registers all named rate limiting policies used by protocol & admin endpoints.
/// Mirrors the prior inline Program.cs configuration without behavioral changes.
/// </summary>
public static class RateLimitingExtensions
{
    public static IServiceCollection AddRateLimitingPolicies(this IServiceCollection services, bool enableGlobalLimiter, IConnectionMultiplexer? redisMux)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            if (enableGlobalLimiter)
            {
                // Same token bucket used previously when Redis present (shared fairly per IP)
                var limiterOptions = new { PermitLimit = 1000, Window = TimeSpan.FromMinutes(1) };
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                {
                    var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
            // token endpoint (partition by client_id when present)
            options.AddPolicy("rl-token", httpContext =>
            {
                var key = ExtractClientIdOrIp(httpContext);
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
            // token exchange (partition by client id/header when present)
            options.AddPolicy("rl-token-exchange", httpContext =>
            {
                string key = ExtractClientIdOrIp(httpContext);
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
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
                var keyBase = ExtractClientIdOrIp(httpContext);
                var key = BucketizeKey(keyBase);

                // If Redis is available, enforce a distributed fixed-window limiter so multi-instance deployments can't bypass limits.
                if (redisMux is not null)
                {
                    return RateLimitPartition.Get(key, _ => new RedisFixedWindowRateLimiter(redisMux, $"par:{key}", new RedisFixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        Prefix = "rl"
                    }));
                }

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
            options.AddPolicy("rl-admin", httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
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

        static string ExtractClientIdOrIp(HttpContext httpContext)
        {
            string key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (httpContext.Request.HasFormContentType)
            {
                try
                {
                    var form = httpContext.Request.ReadFormAsync().GetAwaiter().GetResult();
                    string? cidFromHeader = null;
                    var header = httpContext.Request.Headers.Authorization.ToString();
                    if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.Ordinal))
                    {
                        try
                        {
                            var raw = header.Substring("Basic ".Length).Trim();
                            var bytes = Convert.FromBase64String(raw);
                            var pair = Encoding.UTF8.GetString(bytes);
                            var idx = pair.IndexOf(':');
                            if (idx >= 0) cidFromHeader = pair[..idx];
                        }
                        catch { }
                    }
                    var cid = !string.IsNullOrEmpty(cidFromHeader) ? cidFromHeader : form["client_id"].ToString();
                    if (!string.IsNullOrEmpty(cid)) key = cid;
                }
                catch { }
            }
            return key;
        }

        static string BucketizeKey(string key)
        {
            // Keep Redis keys small + avoid strange chars; stable token suitable for rate-limit partitioning.
            var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(bytes.AsSpan(0, 8));
        }
    }
}
