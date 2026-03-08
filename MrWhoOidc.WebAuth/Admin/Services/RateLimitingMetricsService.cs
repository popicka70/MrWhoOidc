using Microsoft.Extensions.Logging;
using MrWhoOidc.WebAuth.Admin.Dto;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAdmin.Services;

public interface IRateLimitingMetricsService
{
    Task<RateLimitingOverviewDto> GetOverviewAsync(CancellationToken cancellationToken);
}

public sealed class RateLimitingMetricsService : IRateLimitingMetricsService
{
    private readonly IOidcMetrics _metrics;
    private readonly ILogger<RateLimitingMetricsService> _logger;

    public RateLimitingMetricsService(IOidcMetrics metrics, ILogger<RateLimitingMetricsService> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    public Task<RateLimitingOverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        // Note: Counter<long> from System.Diagnostics.Metrics doesn't expose GetValue() directly.
        // In production, these would be read via OpenTelemetry exporters or Prometheus client.
        // For now, we return 0 as placeholder until proper metric aggregation is implemented.
        
        var policies = new List<RateLimitStatusDto>
        {
            new(
                PolicyName: "Token Exchange",
                IsEnabled: true,
                CurrentRequests: 0,
                MaxRequests: null,
                TimeWindow: null,
                WindowResetTime: null
            ),
            new(
                PolicyName: "Token",
                IsEnabled: true,
                CurrentRequests: 0,
                MaxRequests: null,
                TimeWindow: null,
                WindowResetTime: null
            ),
            new(
                PolicyName: "Authorize",
                IsEnabled: true,
                CurrentRequests: 0,
                MaxRequests: null,
                TimeWindow: null,
                WindowResetTime: null
            ),
            new(
                PolicyName: "UserInfo",
                IsEnabled: true,
                CurrentRequests: 0,
                MaxRequests: null,
                TimeWindow: null,
                WindowResetTime: null
            )
        };

        return Task.FromResult<RateLimitingOverviewDto>(new RateLimitingOverviewDto(
            policies,
            Array.Empty<ClientRateLimitDto>(),
            DateTimeOffset.UtcNow,
            0,
            0
        ));
    }
}
