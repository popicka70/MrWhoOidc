using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Background;

/// <summary>
/// Configuration for back-channel logout alert sampling.
/// </summary>
public sealed class BackchannelAlertOptions
{
    /// <summary>Enable alert sampling background service.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>Failure rate percentage (failed / total * 100) over the <see cref="LookbackMinutes"/> window that constitutes a breach.</summary>
    public int FailureRatePercent { get; set; } = 5;
    /// <summary>P95 latency (ms) threshold for delivery attempts over the lookback window.</summary>
    public int LatencyP95Ms { get; set; } = 2000;
    /// <summary>Pending outbox backlog threshold (count of pending notifications ready to send).</summary>
    public int OutboxBacklogThreshold { get; set; } = 50;
    /// <summary>
    /// Number of consecutive minutes a metric must remain in breach before first alert emission.
    /// Set to 0 to emit immediately (single-sample mode) while still honoring <see cref="CooldownSeconds"/>.
    /// </summary>
    public int ConsecutiveMinutes { get; set; } = 5;
    /// <summary>Interval between samples (seconds). Clamped internally to [10,300].</summary>
    public int SampleIntervalSeconds { get; set; } = 60;
    /// <summary>Lookback window (minutes) for failure rate / latency calculations.</summary>
    public int LookbackMinutes { get; set; } = 10;
    /// <summary>Cooldown (seconds) after emitting an alert for a metric before emitting again if still breaching.</summary>
    public int CooldownSeconds { get; set; } = 300;
}

/// <summary>Snapshot of internal alert sampler state for diagnostics.</summary>
public sealed record BackchannelAlertSamplerSnapshot(
    DateTimeOffset CapturedAt,
    int RequiredSamples,
    int CooldownSeconds,
    IReadOnlyDictionary<string, int> BreachSamples,
    IReadOnlyDictionary<string, DateTimeOffset> FirstBreachAt,
    IReadOnlyDictionary<string, DateTimeOffset> LastEmitAt);

public interface IBackchannelAlertDiagnostics
{
    BackchannelAlertSamplerSnapshot GetSnapshot();
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
    ISystemClock clock) : BackgroundService, IBackchannelAlertDiagnostics
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

        double failureRate;
        List<double> allLatencies;
        long totalEmitted, totalFailed;
        lock (_gate)
        {
            // Use snapshot-based rate (latest aggregate) for stability instead of sum of deltas, while still keeping latency window aggregation
            totalEmitted = emitted; // current snapshot over lookback
            totalFailed = failed;
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
    private readonly Dictionary<string, DateTimeOffset> _lastEmitAt = new(StringComparer.Ordinal);
    private readonly object _stateGate = new();

    private IEnumerable<(string type, object payload)> EvaluateAlertsWithSustained(double failureRate, double p95, long backlog, BackchannelAlertOptions cfg, DateTimeOffset now)
    {
        int requiredSamples = Math.Max(1, (int)Math.Ceiling((cfg.ConsecutiveMinutes * 60.0) / Math.Clamp(cfg.SampleIntervalSeconds, 10, 300)));
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, cfg.CooldownSeconds));

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
                lock (_stateGate)
                {
                    _breachSamples.Remove(metric);
                    _firstBreachAt.Remove(metric);
                    _lastEmitAt.Remove(metric);
                }
                continue;
            }

            // Fast-path: if sustain requirement collapses to a single sample, emit immediately (cooldown still enforced)
            if (requiredSamples == 1)
            {
                DateTimeOffset lastImmediate;
                bool hasLast;
                lock (_stateGate) hasLast = _lastEmitAt.TryGetValue(metric, out lastImmediate);
                var shouldEmitImmediate = !hasLast || cooldown == TimeSpan.Zero || (now - lastImmediate >= cooldown);
                if (shouldEmitImmediate)
                {
                    lock (_stateGate)
                    {
                        _breachSamples[metric] = 1;
                        _firstBreachAt[metric] = now;
                        _lastEmitAt[metric] = now;
                    }
                    switch (metric)
                    {
                        case "failure":
                            yield return ("bcl.alert.failure_rate", new { failure_rate = Math.Round(failureRate, 2), threshold = cfg.FailureRatePercent, window_min = cfg.LookbackMinutes, sustained_samples = 1, cooldown_sec = cfg.CooldownSeconds });
                            break;
                        case "latency":
                            yield return ("bcl.alert.latency_p95", new { p95_ms = (int)p95, threshold = cfg.LatencyP95Ms, window_min = cfg.LookbackMinutes, sustained_samples = 1, cooldown_sec = cfg.CooldownSeconds });
                            break;
                        case "backlog":
                            yield return ("bcl.alert.backlog", new { backlog, threshold = cfg.OutboxBacklogThreshold, sustained_samples = 1, cooldown_sec = cfg.CooldownSeconds });
                            break;
                    }
                }
                // Skip sustained logic since we've handled single-sample emission
                continue;
            }

            if (!_breachSamples.TryGetValue(metric, out var count))
            {
                lock (_stateGate)
                {
                    _breachSamples[metric] = 1;
                    _firstBreachAt[metric] = now;
                }
            }
            else
            {
                lock (_stateGate) _breachSamples[metric] = count + 1;
            }

            int currentSamples;
            lock (_stateGate) currentSamples = _breachSamples[metric];
            if (currentSamples >= requiredSamples)
            {
                DateTimeOffset last;
                bool hasLastEmit;
                lock (_stateGate) hasLastEmit = _lastEmitAt.TryGetValue(metric, out last);
                var shouldEmit = !hasLastEmit || (cooldown == TimeSpan.Zero) || (now - last >= cooldown);
                if (shouldEmit)
                {
                    lock (_stateGate) _lastEmitAt[metric] = now;
                    switch (metric)
                    {
                        case "failure":
                            int sustainedF;
                            lock (_stateGate) sustainedF = _breachSamples[metric];
                            yield return ("bcl.alert.failure_rate", new { failure_rate = Math.Round(failureRate, 2), threshold = cfg.FailureRatePercent, window_min = cfg.LookbackMinutes, sustained_samples = sustainedF, cooldown_sec = cfg.CooldownSeconds });
                            break;
                        case "latency":
                            int sustainedL;
                            lock (_stateGate) sustainedL = _breachSamples[metric];
                            yield return ("bcl.alert.latency_p95", new { p95_ms = (int)p95, threshold = cfg.LatencyP95Ms, window_min = cfg.LookbackMinutes, sustained_samples = sustainedL, cooldown_sec = cfg.CooldownSeconds });
                            break;
                        case "backlog":
                            int sustainedB;
                            lock (_stateGate) sustainedB = _breachSamples[metric];
                            yield return ("bcl.alert.backlog", new { backlog, threshold = cfg.OutboxBacklogThreshold, sustained_samples = sustainedB, cooldown_sec = cfg.CooldownSeconds });
                            break;
                    }
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

    public BackchannelAlertSamplerSnapshot GetSnapshot()
    {
        var cfg = _options.CurrentValue;
        var now = _clock.UtcNow;
        int requiredSamples = Math.Max(1, (int)Math.Ceiling((cfg.ConsecutiveMinutes * 60.0) / Math.Clamp(cfg.SampleIntervalSeconds, 10, 300)));
        lock (_stateGate)
        {
            return new BackchannelAlertSamplerSnapshot(
                CapturedAt: now,
                RequiredSamples: requiredSamples,
                CooldownSeconds: cfg.CooldownSeconds,
                BreachSamples: new Dictionary<string, int>(_breachSamples),
                FirstBreachAt: new Dictionary<string, DateTimeOffset>(_firstBreachAt),
                LastEmitAt: new Dictionary<string, DateTimeOffset>(_lastEmitAt));
        }
    }
}
