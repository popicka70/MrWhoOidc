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

        // Client-authenticated OAuth endpoints (and tenant-prefixed equivalents). The client_id is not yet
        // authenticated here, so the partition is tenant + client_id + caller IP (see RateLimitPartitionKeys).
        var oauthPolicy = IsEndpoint(path, "/token") ? "token"
            : IsEndpoint(path, "/introspect") ? "introspect"
            : IsEndpoint(path, "/par") ? "par"
            : IsEndpoint(path, "/revoke") ? "revoke"
            : null;
        if (oauthPolicy is not null)
        {
            var form = await TryReadFormAsync(context);
            if (form is not null)
            {
                var formClientId = form["client_id"].ToString();
                if (!string.IsNullOrEmpty(formClientId))
                {
                    // Lets synchronous limiter partitions (e.g. rl-par) use the value without reading the body.
                    context.Items[RateLimitPartitionKeys.FormClientIdItemKey] = formClientId;
                }
            }

            var limit = 60;
            if (oauthPolicy == "token")
            {
                var isExchange = string.Equals(form?["grant_type"].ToString(), "urn:ietf:params:oauth:grant-type:token-exchange", StringComparison.Ordinal);
                oauthPolicy = isExchange ? "token-exchange" : "token";
                limit = isExchange ? 40 : 100;
            }
            else if (oauthPolicy == "introspect")
            {
                limit = 80;
            }

            var key = RateLimitPartitionKeys.ForClient(context, RateLimitPartitionKeys.GetClientId(context));
            var (allowed, retryAfter, remaining, effectiveLimit, resetAt) = await TryConsumeAsync(oauthPolicy, key, limit, TimeSpan.FromMinutes(1));
            if (!allowed)
            {
                WriteRateLimitHeaders(context, retryAfter, remaining, effectiveLimit, resetAt);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsync("Too Many Requests");
                return;
            }
        }

        // --- Delegated Access rate limits ---

        // Rate-limit delegation create: /account/delegated-access/create (POST)
        if (IsEndpoint(path, "/account/delegated-access/create"))
        {
            string key = ExtractClientId(context) ?? ExtractIp(context) ?? "unknown";
            var (allowed, retryAfter, remaining, limit, resetAt) = await TryConsumeAsync("delegation-create", key, 5, TimeSpan.FromMinutes(1));
            if (!allowed)
            {
                WriteRateLimitHeaders(context, retryAfter, remaining, limit, resetAt);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsync("Too Many Requests");
                return;
            }
        }

        // Rate-limit delegation accept: /account/delegated-access/invitations/{token} (POST)
        // Match prefix since the token segment is variable.
        if (path.StartsWith("/account/delegated-access/invitations/", StringComparison.Ordinal))
        {
            string key = ExtractClientId(context) ?? ExtractIp(context) ?? "unknown";
            var (allowed, retryAfter, remaining, limit, resetAt) = await TryConsumeAsync("delegation-accept", key, 10, TimeSpan.FromMinutes(1));
            if (!allowed)
            {
                WriteRateLimitHeaders(context, retryAfter, remaining, limit, resetAt);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsync("Too Many Requests");
                return;
            }
        }

        // Rate-limit delegation activate: /account/delegated-access/{id}/activate (POST)
        // Match the fixed suffix after the variable grant-id segment.
        if (path.EndsWith("/activate", StringComparison.Ordinal) && path.Contains("/delegated-access/", StringComparison.Ordinal))
        {
            string key = ExtractClientId(context) ?? ExtractIp(context) ?? "unknown";
            var (allowed, retryAfter, remaining, limit, resetAt) = await TryConsumeAsync("delegation-activate", key, 20, TimeSpan.FromMinutes(1));
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

    private static string? ExtractClientId(HttpContext ctx) => RateLimitPartitionKeys.GetClientId(ctx);

    private static async Task<IFormCollection?> TryReadFormAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.HasFormContentType)
        {
            return null;
        }

        try
        {
            // Buffered by ASP.NET Core, so the endpoint handler reads the same form without re-reading the body.
            return await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return null; // malformed body: limit by tenant + IP only; the endpoint rejects it later
        }
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
            // Reset time is end-of-bucket (deterministic), not "now + ttl" (which can be tiny
            // if the key was just refreshed by another process). This guarantees a positive
            // Retry-After on every 429.
            var resetAt = DateTimeOffset.FromUnixTimeSeconds((bucket + 1) * (long)window.TotalSeconds);
            var retry = allowed ? (TimeSpan?)null : (resetAt - now);
            if (retry is { TotalSeconds: < 1 })
            {
                // Floor at 1 second so clients always have a positive Retry-After.
                retry = TimeSpan.FromSeconds(1);
            }
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
