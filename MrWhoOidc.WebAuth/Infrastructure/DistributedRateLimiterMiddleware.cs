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

    // Lua script: atomically increments the counter and sets a TTL on first creation.
    // Avoids a race between INCR and a subsequent EXPIRE call where a server crash
    // between the two would leave an immortal key (permanent rate-limit block).
    private static readonly string IncrWithTtlScript = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        return current
        """;

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

        // Rate-limit the token endpoint (and tenant-prefixed equivalents).
        if (IsEndpoint(path, "/token"))
        {
            string clientId = ExtractClientId(context) ?? ExtractIp(context) ?? "unknown";
            bool isExchange = false;
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType)
            {
                try
                {
                    var form = await context.Request.ReadFormAsync(context.RequestAborted);
                    var grantType = form["grant_type"].ToString();
                    isExchange = string.Equals(grantType, "urn:ietf:params:oauth:grant-type:token-exchange", StringComparison.Ordinal);
                }
                catch { /* treat as non-exchange if form cannot be read */ }
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
        else if (IsEndpoint(path, "/introspect"))
        {
            // Partition introspect by client_id too, falling back to IP only when unavailable.
            string key = ExtractClientId(context) ?? ExtractIp(context) ?? "unknown";
            var (allowed, retryAfter, remaining, limit, resetAt) = await TryConsumeAsync("introspect", key, 80, TimeSpan.FromMinutes(1));
            if (!allowed)
            {
                WriteRateLimitHeaders(context, retryAfter, remaining, limit, resetAt);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsync("Too Many Requests");
                return;
            }
        }
        else if (IsEndpoint(path, "/par"))
        {
            string key = ExtractClientId(context) ?? ExtractIp(context) ?? "unknown";
            var (allowed, retryAfter, remaining, limit, resetAt) = await TryConsumeAsync("par", key, 60, TimeSpan.FromMinutes(1));
            if (!allowed)
            {
                WriteRateLimitHeaders(context, retryAfter, remaining, limit, resetAt);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsync("Too Many Requests");
                return;
            }
        }
        else if (IsEndpoint(path, "/revoke"))
        {
            string key = ExtractClientId(context) ?? ExtractIp(context) ?? "unknown";
            var (allowed, retryAfter, remaining, limit, resetAt) = await TryConsumeAsync("revoke", key, 60, TimeSpan.FromMinutes(1));
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

    /// <summary>
    /// Returns true if the request path matches the given endpoint, accounting for
    /// tenant-prefixed paths (e.g. /t/{slug}/token as well as /token).
    /// </summary>
    private static bool IsEndpoint(string lowerPath, string endpoint)
    {
        if (lowerPath == endpoint) return true;
        // Tenant-prefixed: /t/{slug}{endpoint}
        if (lowerPath.StartsWith("/t/", StringComparison.Ordinal))
        {
            var afterSlug = lowerPath.IndexOf('/', 3); // skip past /t/
            if (afterSlug >= 0)
            {
                var suffix = lowerPath[afterSlug..];
                if (suffix == endpoint) return true;
            }
        }
        return false;
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
        // client_id from form body: Only use if the form was already buffered;
        // avoid synchronous/blocking ReadFormAsync which causes thread-pool starvation.
        if (ctx.Request.HasFormContentType && ctx.Items.TryGetValue("__form_client_id", out var cached) && cached is string cid)
        {
            return cid;
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
            var ttlSeconds = (long)window.TotalSeconds;

            // Atomic INCR + conditional EXPIRE via Lua to prevent immortal keys.
            var count = (long)await db.ScriptEvaluateAsync(
                IncrWithTtlScript,
                keys: [(RedisKey)redisKey],
                values: [(RedisValue)ttlSeconds]);

            var ttl = await db.KeyTimeToLiveAsync(redisKey) ?? window;
            var allowed = count <= limit;
            var remaining = Math.Max(0, limit - count);
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
