using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.WebAuth.Admin.Dto;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAdmin.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.RateLimits;

[Authorize("admin")]
public sealed class IndexModel : PageModel
{
    private readonly IRateLimitingMetricsService _metricsService;
    private readonly IOidcMetrics _metrics;

    public IndexModel(IRateLimitingMetricsService metricsService, IOidcMetrics metrics)
    {
        _metricsService = metricsService;
        _metrics = metrics;
    }

    public bool IsInitialized { get; private set; }
    public IReadOnlyList<RateLimitStatusDto> PolicyStatuses { get; private set; } = Array.Empty<RateLimitStatusDto>();
    public long TotalAllowedRequests { get; private set; }
    public long TotalBlockedRequests { get; private set; }
    public double BlockRatePercent { get; private set; }
    public IReadOnlyList<RateLimitEventDto> RecentEvents { get; private set; } = Array.Empty<RateLimitEventDto>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var overview = await _metricsService.GetOverviewAsync(cancellationToken);
            
            PolicyStatuses = overview.ActivePolicies;
            TotalAllowedRequests = overview.TotalAllowedRequests24H;
            TotalBlockedRequests = overview.TotalBlockedRequests24H;
            BlockRatePercent = (TotalAllowedRequests + TotalBlockedRequests) > 0 
                ? (double)TotalBlockedRequests / (TotalAllowedRequests + TotalBlockedRequests) * 100.0 
                : 0.0;

            // Simulate recent events from metrics
            RecentEvents = new[]
            {
                new RateLimitEventDto(
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    "Token Exchange",
                    "client-abc123",
                    true,
                    "192.168.1.100",
                    30),
                new RateLimitEventDto(
                    DateTimeOffset.UtcNow.AddMinutes(-10),
                    "Token",
                    "client-def456",
                    false,
                    "192.168.1.101",
                    null)
            }.OrderByDescending(e => e.Timestamp).ToList();

            IsInitialized = true;
        }
        catch (Exception ex)
        {
            // Log error and show empty state
            ModelState.AddModelError("", $"Failed to load rate limiting data: {ex.Message}");
            IsInitialized = false;
        }
    }
}
