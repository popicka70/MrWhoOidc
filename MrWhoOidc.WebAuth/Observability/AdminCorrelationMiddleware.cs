using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Observability;

public sealed class AdminCorrelationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ICorrelationContextAccessor _accessor;
    private readonly ILogger<AdminCorrelationMiddleware> _logger;

    public AdminCorrelationMiddleware(RequestDelegate next, ICorrelationContextAccessor accessor, ILogger<AdminCorrelationMiddleware> logger)
    {
        _next = next;
        _accessor = accessor;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_accessor.HasCorrelation)
        {
            _logger.LogWarning("Admin API request missing X-Correlation-Id header");
        }
        await _next(context);
    }
}
