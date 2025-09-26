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

public interface ISystemClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : ISystemClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public sealed class BackchannelAlertSampler(
    IDbContextFactory<AuthDbContext> dbFactory,
    OidcMetrics metrics,
    ILogger<BackchannelAlertSampler> logger,
    IAlertPublisher alerts,
    IOptionsMonitor<BackchannelAlertOptions> options,
    MrWhoOidc.WebAuth.Background.BackchannelRuntimeState runtimeState,
    ISystemClock clock) : BackgroundService
{
    private readonly IDbContextFactory<AuthDbContext> _dbFactory = dbFactory;
    private readonly OidcMetrics _metrics = metrics;
    private readonly ILogger<BackchannelAlertSampler> _logger = logger;
    private readonly IAlertPublisher _alerts = alerts;
    private readonly IOptionsMonitor<BackchannelAlertOptions> _options = options;
    private readonly BackchannelRuntimeState _runtime = runtimeState;
    private readonly ISystemClock _clock = clock;

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
                    await TickAsync(stoppingToken);
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

    // Public for tests (invoke individual sampling cycle)
    public async Task TickAsync(CancellationToken ct)
    {
        var cfg = _options.CurrentValue;
        if (!cfg.Enabled) return;

        // We derive emitted/failed counts from DB since metrics counters are cumulative but not directly accessible per client here.
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _clock.UtcNow;
        var lookbackStart = now.AddMinutes(-cfg.LookbackMinutes);

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
            _window.Enqueue((now, emittedDelta, failedDelta, new List<double>(latencies)));
            while (_window.Count > 0 && _window.Peek().ts < now.AddMinutes(-cfg.LookbackMinutes))
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

        foreach (var alert in EvaluateAlertsWithSustained(failureRate, p95, backlog, cfg, now))
        {
            await _alerts.PublishAsync(alert.type, alert.payload, ct);
        }
    }

    // Sustained breach tracking
    private readonly Dictionary<string, int> _breachSamples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _firstBreachAt = new(StringComparer.Ordinal);

    private IEnumerable<(string type, object payload)> EvaluateAlertsWithSustained(double failureRate, double p95, long backlog, BackchannelAlertOptions cfg, DateTimeOffset now)
    {
        int requiredSamples = Math.Max(1, (int)Math.Ceiling((cfg.ConsecutiveMinutes * 60.0) / Math.Clamp(cfg.SampleIntervalSeconds, 10, 300)));

        foreach (var metric in new[] { "failure", "latency", "backlog" })
        {
            bool breach = metric switch
            {
                "failure" => failureRate >= cfg.FailureRatePercent && cfg.FailureRatePercent > 0,
                "latency" => p95 >= cfg.LatencyP95Ms && p95 > 0 && cfg.LatencyP95Ms > 0,
                "backlog" => backlog >= cfg.OutboxBacklogThreshold && cfg.OutboxBacklogThreshold > 0,
                _ => false
            };

            if (!breach)
            {
                _breachSamples.Remove(metric);
                _firstBreachAt.Remove(metric);
                continue;
            }

            if (!_breachSamples.TryGetValue(metric, out var count))
            {
                _breachSamples[metric] = 1;
                _firstBreachAt[metric] = now;
            }
            else
            {
                _breachSamples[metric] = count + 1;
            }

            if (_breachSamples[metric] >= requiredSamples)
            {
                // Emit and keep counting (continuous alert events each sample once sustained). Could add cooldown later.
                switch (metric)
                {
                    case "failure":
                        yield return ("bcl.alert.failure_rate", new { failure_rate = Math.Round(failureRate, 2), threshold = cfg.FailureRatePercent, window_min = cfg.LookbackMinutes, sustained_samples = _breachSamples[metric] });
                        break;
                    case "latency":
                        yield return ("bcl.alert.latency_p95", new { p95_ms = (int)p95, threshold = cfg.LatencyP95Ms, window_min = cfg.LookbackMinutes, sustained_samples = _breachSamples[metric] });
                        break;
                    case "backlog":
                        yield return ("bcl.alert.backlog", new { backlog, threshold = cfg.OutboxBacklogThreshold, sustained_samples = _breachSamples[metric] });
                        break;
                }
            }
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