using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MrWhoOidc.WebAuth.Infrastructure;

public class DistributedRateLimiterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<DistributedRateLimiterMiddleware> _logger;

    public DistributedRateLimiterMiddleware(RequestDelegate next, IConnectionMultiplexer? redis, ILogger<DistributedRateLimiterMiddleware> logger)
    {
        _next = next;
        _redis = redis;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // No-op if Redis not configured
        if (_redis is null)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value?.ToLowerInvariant();
        if (string.IsNullOrEmpty(path))
        {
            await _next(context);
            return;
        }

        // Only enforce on select endpoints for now
        if (path == "/token")
        {
            // Partition by client_id (from auth header basic or form) if available, else by IP
            string clientId = ExtractClientId(context) ?? ExtractIp(context) ?? "unknown";
            bool isExchange = false;
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType)
            {
                try
                {
                    var form = await context.Request.ReadFormAsync();
                    var grantType = form["grant_type"].ToString();
                    isExchange = string.Equals(grantType, "urn:ietf:params:oauth:grant-type:token-exchange", StringComparison.Ordinal);
                }
                catch { /* ignore */ }
            }

            var policy = isExchange ? "token-exchange" : "token";
            var (allowed, retryAfter, remaining, limit, resetAt) = await TryConsumeAsync(policy, clientId, isExchange ? 40 : 100, TimeSpan.FromMinutes(1));
            if (!allowed)
            {
                WriteRateLimitHeaders(context, retryAfter, remaining, limit, resetAt);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsync("Too Many Requests");
                return;
            }
        }
        else if (path == "/introspect")
        {
            string key = ExtractIp(context) ?? "unknown";
            var (allowed, retryAfter, remaining, limit, resetAt) = await TryConsumeAsync("introspect", key, 80, TimeSpan.FromMinutes(1));
            if (!allowed)
            {
                WriteRateLimitHeaders(context, retryAfter, remaining, limit, resetAt);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsync("Too Many Requests");
                return;
            }
        }

        await _next(context);
    }

    private static string? ExtractIp(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.ToString();

    private static string? ExtractClientId(HttpContext ctx)
    {
        // Authorization: Basic base64(clientId:secret)
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            try
            {
                var raw = header.Substring("Basic ".Length).Trim();
                var bytes = Convert.FromBase64String(raw);
                var pair = System.Text.Encoding.UTF8.GetString(bytes);
                var idx = pair.IndexOf(':');
                if (idx >= 0) return pair[..idx];
            }
            catch { /* ignore */ }
        }
        if (ctx.Request.HasFormContentType)
        {
            try
            {
                var form = ctx.Request.ReadFormAsync().GetAwaiter().GetResult();
                var cid = form["client_id"].ToString();
                if (!string.IsNullOrEmpty(cid)) return cid;
            }
            catch { /* ignore */ }
        }
        return null;
    }

    private async Task<(bool allowed, TimeSpan? retryAfter, long remaining, long limit, DateTimeOffset resetAt)> TryConsumeAsync(string policy, string keyBase, int limit, TimeSpan window)
    {
        try
        {
            var db = _redis!.GetDatabase();
            var now = DateTimeOffset.UtcNow;
            var bucket = now.ToUnixTimeSeconds() / (long)window.TotalSeconds;
            var redisKey = $"rl:{policy}:{keyBase}:{bucket}";

            var count = await db.StringIncrementAsync(redisKey);
            if (count == 1)
            {
                await db.KeyExpireAsync(redisKey, window);
            }
            var ttl = await db.KeyTimeToLiveAsync(redisKey) ?? window;
            var allowed = count <= limit;
            var remaining = Math.Max(0, limit - (long)count);
            var resetAt = now.Add(ttl);
            var retry = allowed ? (TimeSpan?)null : ttl;
            return (allowed, retry, remaining, limit, resetAt);
        }
        catch (Exception ex)
        {
            // Fail-open on Redis errors; log once per policy/key pair (rate-limited by logger config)
            _logger.LogWarning(ex, "Distributed rate limiter error for {Policy}/{Key}", policy, keyBase);
            return (true, null, limit, limit, DateTimeOffset.UtcNow);
        }
    }

    private static void WriteRateLimitHeaders(HttpContext context, TimeSpan? retryAfter, long remaining, long limit, DateTimeOffset resetAt)
    {
        if (retryAfter.HasValue)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.Value.TotalSeconds));
            context.Response.Headers["Retry-After"] = seconds.ToString(CultureInfo.InvariantCulture);
        }
        context.Response.Headers["X-RateLimit-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, remaining).ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-RateLimit-Reset"] = ((long)resetAt.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture);
    }
}
