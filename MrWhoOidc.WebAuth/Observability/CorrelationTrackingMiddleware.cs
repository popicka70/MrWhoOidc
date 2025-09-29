using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Observability;

public sealed class CorrelationTrackingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ICorrelationContextAccessor _accessor;
    private readonly ICorrelationIdGenerator _generator;
    private readonly ICorrelationStateCache _stateCache;
    private readonly ILogger<CorrelationTrackingMiddleware> _logger;

    public CorrelationTrackingMiddleware(RequestDelegate next,
        ICorrelationContextAccessor accessor,
        ICorrelationIdGenerator generator,
        ICorrelationStateCache stateCache,
        ILogger<CorrelationTrackingMiddleware> logger)
    {
        _next = next;
        _accessor = accessor;
        _generator = generator;
        _stateCache = stateCache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? correlationId = _accessor.CorrelationId;
        var fromHeader = false;

        if (string.IsNullOrEmpty(correlationId))
        {
            var header = context.Request.Headers["X-Correlation-Id"].ToString();
            if (!string.IsNullOrEmpty(header))
            {
                if (IsValidHeader(header))
                {
                    correlationId = header;
                    fromHeader = true;
                }
                else
                {
                    _logger.LogWarning("Ignoring invalid X-Correlation-Id header value.");
                }
            }
        }

        if (string.IsNullOrEmpty(correlationId))
        {
            var handle = context.Request.Query["cid_ref"].ToString();
            if (!string.IsNullOrEmpty(handle) && LooksLikeHandle(handle))
            {
                try
                {
                    correlationId = await _stateCache.TryGetAsync(handle, consume: false, context.RequestAborted);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve correlation handle from cache");
                }
            }
        }

        var path = context.Request.Path;
        var requiresCorrelation = path.Equals("/authorize", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(correlationId) && requiresCorrelation)
        {
            correlationId = _generator.GenerateCorrelationId();
            _logger.LogDebug("Generated new correlation id {CorrelationId} for /authorize", correlationId);
        }

        if (!string.IsNullOrEmpty(correlationId))
        {
            using (CorrelationLogging.BeginScope(_logger, correlationId))
            {
                _accessor.Set(correlationId, fromHeader);
                await _next(context);
            }
        }
        else
        {
            await _next(context);
        }
    }

    private static bool IsValidHeader(string value)
    {
        if (value.Length > 64) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsLetterOrDigit(ch)) continue;
            if (ch is '-' or '_') continue;
            return false;
        }
        return true;
    }

    private static bool LooksLikeHandle(string value)
    {
        if (value.Length is < 8 or > 64) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsLetterOrDigit(ch)) continue;
            if (ch is '-' or '_') continue;
            return false;
        }
        return true;
    }
}
