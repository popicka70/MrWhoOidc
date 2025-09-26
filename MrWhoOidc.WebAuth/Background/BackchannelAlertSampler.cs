using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Background;

public sealed class BackchannelAlertOptions
{
    public bool Enabled { get; set; } = false;
    public int FailureRatePercent { get; set; } = 5; // percent over window
    public int LatencyP95Ms { get; set; } = 2000;
    public int OutboxBacklogThreshold { get; set; } = 50;
    public int ConsecutiveMinutes { get; set; } = 5; // sustained condition before alert
    public int SampleIntervalSeconds { get; set; } = 60; // 1 minute
    public int LookbackMinutes { get; set; } = 10; // window for rate
}

public sealed class BackchannelAlertSampler(
    IDbContextFactory<AuthDbContext> dbFactory,
    OidcMetrics metrics,
    ILogger<BackchannelAlertSampler> logger,
    IAlertPublisher alerts,
    IOptionsMonitor<BackchannelAlertOptions> options,
    MrWhoOidc.WebAuth.Background.BackchannelRuntimeState runtimeState) : BackgroundService
{
    private readonly IDbContextFactory<AuthDbContext> _dbFactory = dbFactory;
    private readonly OidcMetrics _metrics = metrics;
    private readonly ILogger<BackchannelAlertSampler> _logger = logger;
    private readonly IAlertPublisher _alerts = alerts;
    private readonly IOptionsMonitor<BackchannelAlertOptions> _options = options;
    private readonly BackchannelRuntimeState _runtime = runtimeState;

    // Rolling counters (approximate) since process start – we compute deltas per interval
    private long _lastEmitted;
    private long _lastFailed;
    private readonly Queue<(DateTimeOffset ts, long emittedDelta, long failedDelta, List<double> latencies)> _window = new();
    private readonly object _gate = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cfg = _options.CurrentValue;
                if (cfg.Enabled)
                {
                    await SampleAsync(cfg, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Backchannel alert sampler error");
            }
            var delay = TimeSpan.FromSeconds(Math.Clamp(_options.CurrentValue.SampleIntervalSeconds, 10, 300));
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task SampleAsync(BackchannelAlertOptions cfg, CancellationToken ct)
    {
        // We derive emitted/failed counts from DB since metrics counters are cumulative but not directly accessible per client here.
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var lookbackStart = DateTimeOffset.UtcNow.AddMinutes(-cfg.LookbackMinutes);

        var emitted = await db.BackchannelLogoutNotifications
            .LongCountAsync(n => n.LastAttemptAt >= lookbackStart, ct);
        var failed = await db.BackchannelLogoutNotifications
            .LongCountAsync(n => (n.Status == "failed" || n.Status == "dead_letter") && n.LastAttemptAt >= lookbackStart, ct);

        // Latency approximation: use recent succeeded notifications' attempt time difference (LastAttemptAt - CreatedAt)
        var recentSuccess = await db.BackchannelLogoutNotifications
            .Where(n => n.Status == "succeeded" && n.LastAttemptAt >= lookbackStart)
            .Select(n => new { n.CreatedAt, n.LastAttemptAt })
            .ToListAsync(ct);
        var latencies = new List<double>(recentSuccess.Count);
        foreach (var r in recentSuccess)
        {
            // If CreatedAt or LastAttemptAt missing, skip
            if (r.CreatedAt == default || r.LastAttemptAt == null) continue;
            var span = r.LastAttemptAt.Value - r.CreatedAt;
            if (span.TotalMilliseconds >= 0 && span.TotalMilliseconds < 600000)
                latencies.Add(span.TotalMilliseconds);
        }

        lock (_gate)
        {
            var emittedDelta = emitted - _lastEmitted;
            var failedDelta = failed - _lastFailed;
            _lastEmitted = emitted;
            _lastFailed = failed;
            // store a copy of latency list for this sample
            _window.Enqueue((DateTimeOffset.UtcNow, emittedDelta, failedDelta, new List<double>(latencies)));
            while (_window.Count > 0 && _window.Peek().ts < DateTimeOffset.UtcNow.AddMinutes(-cfg.LookbackMinutes))
                _window.Dequeue();
        }

        double failureRate = 0;
        List<double> allLatencies;
        long totalEmitted, totalFailed;
        lock (_gate)
        {
            totalEmitted = _window.Sum(x => x.emittedDelta);
            totalFailed = _window.Sum(x => x.failedDelta);
            failureRate = totalEmitted > 0 ? (double)totalFailed / totalEmitted * 100 : 0;
            allLatencies = _window.SelectMany(x => x.latencies).ToList();
        }

        var p95 = ComputeP95(allLatencies);
        var backlog = _runtime.PendingBacklog;

        foreach (var alert in EvaluateAlerts(failureRate, p95, backlog, cfg, ct))
        {
            await _alerts.PublishAsync(alert.type, alert.payload, ct);
        }
    }

    private IEnumerable<(string type, object payload)> EvaluateAlerts(double failureRate, double p95, long backlog, BackchannelAlertOptions cfg, CancellationToken ct)
    {
        // Simple threshold checks; external system handles dedupe. Could add stateful suppression if needed.
        if (failureRate >= cfg.FailureRatePercent)
        {
            yield return ("bcl.alert.failure_rate", new { failure_rate = Math.Round(failureRate, 2), threshold = cfg.FailureRatePercent, window_min = cfg.LookbackMinutes });
        }
        if (p95 >= cfg.LatencyP95Ms && p95 > 0)
        {
            yield return ("bcl.alert.latency_p95", new { p95_ms = (int)p95, threshold = cfg.LatencyP95Ms, window_min = cfg.LookbackMinutes });
        }
        if (backlog >= cfg.OutboxBacklogThreshold)
        {
            yield return ("bcl.alert.backlog", new { backlog, threshold = cfg.OutboxBacklogThreshold });
        }
    }

    private static double ComputeP95(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var idx = (int)Math.Ceiling(values.Count * 0.95) - 1;
        idx = Math.Clamp(idx, 0, values.Count - 1);
        return values[idx];
    }
}