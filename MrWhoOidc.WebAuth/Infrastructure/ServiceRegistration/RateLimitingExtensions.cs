using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

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
            // token endpoint
            options.AddPolicy("rl-token", httpContext =>
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
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
    }
}
