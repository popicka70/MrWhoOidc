using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Background;

public sealed class BackchannelDispatchOptions
{
    public int MaxDegreeOfParallelism { get; set; } = 5;
    public int MaxAttempts { get; set; } = 5;
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(10);
    public int CircuitOpenFailures { get; set; } = 5;
    public TimeSpan CircuitOpenDuration { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class BackchannelFeatureOptions
{
    // Global feature flag to enable/disable BCL processing and enqueueing
    public bool Enabled { get; set; } = true;
    // Simple alerting thresholds (logs warnings when exceeded)
    public int AlertBacklogThreshold { get; set; } = 100;
    public int AlertOpenCircuitThreshold { get; set; } = 5;
}

public sealed record CircuitStateSnapshot(int Failures, DateTimeOffset? OpenUntil);

// Runtime state for health inspection
public sealed class BackchannelRuntimeState
{
    public long PendingBacklog { get; set; }
    public ConcurrentDictionary<string, CircuitStateSnapshot> Circuits { get; } = new(StringComparer.Ordinal);
    public bool EmissionEnabled { get; set; } = true;
}

internal sealed class CircuitState
{
    public int Failures;
    public DateTimeOffset? OpenUntil;
}

public sealed class BackchannelLogoutDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackchannelLogoutDispatcher> _logger;
    private readonly IOidcMetrics _metrics;
    private readonly IAlertPublisher _alerts;
    private readonly MrWhoOidc.WebAuth.Observability.IAuditSink _audit;
    private readonly BackchannelDispatchOptions _options;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<BackchannelFeatureOptions> _feature;
    private readonly BackchannelRuntimeState _state;
    private readonly Dictionary<string, CircuitState> _circuits = new(StringComparer.Ordinal);

    public BackchannelLogoutDispatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<BackchannelLogoutDispatcher> logger,
        IOidcMetrics metrics,
        IAlertPublisher alerts,
        MrWhoOidc.WebAuth.Observability.IAuditSink audit,
        BackchannelDispatchOptions options,
        Microsoft.Extensions.Options.IOptionsMonitor<BackchannelFeatureOptions> feature,
        BackchannelRuntimeState state)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
        _alerts = alerts;
        _audit = audit;
        _options = options;
        _feature = feature;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var http = NetworkSecurity.CreateSafeHttpClient(TimeSpan.FromSeconds(5));

        // Startup delay to allow migrations to complete
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Respect feature flag and update runtime state
                var enabled = _feature.CurrentValue.Enabled;
                _state.EmissionEnabled = enabled;
                if (!enabled)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                if (!await BackgroundServiceTenantHelper.TrySetDefaultTenantContextAsync(scope, stoppingToken))
                {
                    _logger.LogWarning("Backchannel dispatcher could not resolve the default tenant context");
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                var now = DateTimeOffset.UtcNow;
                // capture backlog size for health
                _state.PendingBacklog = await db.BackchannelLogoutNotifications
                    .AsNoTracking()
                    .LongCountAsync(n => n.Status == "pending" && (n.NextAttemptAt == null || n.NextAttemptAt <= now), stoppingToken);
                _metrics.SetBclBacklog(_state.PendingBacklog);
                if (_state.PendingBacklog > _feature.CurrentValue.AlertBacklogThreshold)
                {
                    _logger.LogWarning("BCL backlog high: {Backlog} > threshold {Threshold}", _state.PendingBacklog, _feature.CurrentValue.AlertBacklogThreshold);
                    await _alerts.PublishAsync("bcl.backlog.high", new { backlog = _state.PendingBacklog, threshold = _feature.CurrentValue.AlertBacklogThreshold }, stoppingToken);
                }
                var batchIds = await db.BackchannelLogoutNotifications
                    .AsNoTracking()
                    .Where(n => n.Status == "pending" && (n.NextAttemptAt == null || n.NextAttemptAt <= now))
                    .OrderBy(n => n.CreatedAt)
                    .Select(n => n.Id)
                    .Take(_options.MaxDegreeOfParallelism * 10)
                    .ToListAsync(stoppingToken);

                if (batchIds.Count == 0)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                var sem = new SemaphoreSlim(_options.MaxDegreeOfParallelism);
                var tasks = batchIds.Select(id => ProcessOneAsync(http, id, sem, stoppingToken)).ToArray();
                await Task.WhenAll(tasks);

                // Alert on circuits after batch
                var openCircuits = _circuits.Values.Count(c => c.OpenUntil is not null && c.OpenUntil > DateTimeOffset.UtcNow);
                if (openCircuits > _feature.CurrentValue.AlertOpenCircuitThreshold)
                {
                    _logger.LogWarning("BCL circuits open: {Count} > threshold {Threshold}", openCircuits, _feature.CurrentValue.AlertOpenCircuitThreshold);
                    await _alerts.PublishAsync("bcl.circuits.open", new { count = openCircuits, threshold = _feature.CurrentValue.AlertOpenCircuitThreshold }, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backchannel dispatcher loop error");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task ProcessOneAsync(HttpClient http, Guid id, SemaphoreSlim sem, CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            if (!await BackgroundServiceTenantHelper.TrySetDefaultTenantContextAsync(scope, ct))
            {
                _logger.LogWarning("Skipping backchannel notification {NotificationId} because the default tenant context is unavailable", id);
                return;
            }

            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var n = await db.BackchannelLogoutNotifications.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (n is null)
            {
                return;
            }

            // Circuit breaker per client
            if (_circuits.TryGetValue(n.ClientId, out var circuit) && circuit.OpenUntil is not null && circuit.OpenUntil > DateTimeOffset.UtcNow)
            {
                _logger.LogDebug("Circuit open for {ClientId} until {Until}", n.ClientId, circuit.OpenUntil);
                return; // defer until circuit closes
            }

            if (!TryValidateTargetUri(n.TargetUri, out var targetUri))
            {
                n.Status = "dead_letter";
                n.LastError = "Invalid backchannel logout target URI";
                n.LastAttemptAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                _logger.LogWarning("Rejected unsafe BCL target URI for {ClientId}", n.ClientId);
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            HttpResponseMessage? resp = null;
            Exception? lastEx = null;
            var attempt = n.AttemptCount + 1;

            // Audit attempt start
            _audit.Emit("bcl.attempt", new
            {
                client_id = n.ClientId,
                target = targetUri.Host,
                attempt,
                sid_hash = _audit.HashValue(n.Sid),
                sub_hash = _audit.HashValue(n.Sub),
                id = n.Id
            });

            try
            {
                using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("logout_token", n.LogoutToken) });
                resp = await http.PostAsync(targetUri, content, ct);

                if (resp.IsSuccessStatusCode)
                {
                    n.Status = "succeeded";
                    _metrics.TokenSuccess.Add(1, new("kind", "bcl"), new("client_id", n.ClientId));
                    _metrics.BclDelivered.Add(1, new KeyValuePair<string, object?>("client_id", n.ClientId));
                    ResetCircuit(n.ClientId);
                    _audit.Emit("bcl.success", new { client_id = n.ClientId, target = targetUri.Host, attempt, id = n.Id, status = (int)resp.StatusCode });
                }
                else
                {
                    var status = (int)resp.StatusCode;
                    n.LastHttpStatus = status;
                    n.AttemptCount = attempt;
                    lastEx = null;
                    var retriable = status == 408 || status == 429 || status >= 500;
                    if (retriable && attempt < _options.MaxAttempts)
                    {
                        n.Status = "pending";
                        var backoff = ComputeBackoff(attempt);
                        n.NextAttemptAt = DateTimeOffset.UtcNow.Add(backoff);
                        BumpCircuit(n.ClientId);
                        _metrics.TokenFailures.Add(1, new("kind", "bcl"), new("client_id", n.ClientId));
                        _metrics.BclFailed.Add(1, new KeyValuePair<string, object?>("client_id", n.ClientId));
                        _audit.Emit("bcl.retry", new { client_id = n.ClientId, target = targetUri.Host, attempt, http_status = status, next_in_ms = (int)backoff.TotalMilliseconds, id = n.Id });
                    }
                    else
                    {
                        n.Status = attempt >= _options.MaxAttempts ? "dead_letter" : "failed";
                        OpenCircuitMaybe(n.ClientId);
                        _metrics.TokenFailures.Add(1, new("kind", "bcl"), new("client_id", n.ClientId));
                        _metrics.BclFailed.Add(1, new KeyValuePair<string, object?>("client_id", n.ClientId));
                        var t = n.Status == "dead_letter" ? "bcl.dead_letter" : "bcl.fail";
                        _audit.Emit(t, new { client_id = n.ClientId, target = targetUri.Host, attempt, http_status = status, id = n.Id });
                    }
                }
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                lastEx = ex;
                await MarkRetryAsync(n, attempt, ex);
                _audit.Emit("bcl.retry", new { client_id = n.ClientId, target = targetUri.Host, attempt, error = "timeout", id = n.Id });
            }
            catch (HttpRequestException ex)
            {
                lastEx = ex;
                await MarkRetryAsync(n, attempt, ex);
                _audit.Emit("bcl.retry", new { client_id = n.ClientId, target = targetUri.Host, attempt, error = ex.GetType().Name, id = n.Id });
            }
            catch (Exception ex)
            {
                lastEx = ex;
                n.Status = attempt >= _options.MaxAttempts ? "dead_letter" : "failed";
                n.AttemptCount = attempt;
                n.LastError = ex.Message;
                OpenCircuitMaybe(n.ClientId);
                _metrics.TokenFailures.Add(1, new("kind", "bcl"), new("client_id", n.ClientId));
                var t = n.Status == "dead_letter" ? "bcl.dead_letter" : "bcl.fail";
                _audit.Emit(t, new { client_id = n.ClientId, target = targetUri.Host, attempt, error = ex.GetType().Name, id = n.Id });
            }
            finally
            {
                sw.Stop();
                n.LastAttemptAt = DateTimeOffset.UtcNow;
                _metrics.TokenDurationMs.Record(sw.Elapsed.TotalMilliseconds, new("kind", "bcl"), new("client_id", n.ClientId));
                _metrics.BclDeliveryLatencyMs.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("client_id", n.ClientId));
                await db.SaveChangesAsync(ct);
                if (lastEx != null)
                {
                    _logger.LogWarning(lastEx, "BCL delivery error for {ClientId} attempt {Attempt}", n.ClientId, attempt);
                    await _alerts.PublishAsync("bcl.delivery.error", new { n.ClientId, n.TargetUri, attempt, error = lastEx.Message }, ct);
                }
                else if (resp != null && !resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("BCL delivery failed for {ClientId} HTTP {Status} attempt {Attempt}", n.ClientId, (int)resp.StatusCode, attempt);
                    await _alerts.PublishAsync("bcl.delivery.failed", new { n.ClientId, n.TargetUri, status = (int)resp.StatusCode, attempt }, ct);
                }
                else if (resp != null)
                {
                    _logger.LogInformation("BCL delivery succeeded for {ClientId} in {Ms}ms", n.ClientId, sw.ElapsedMilliseconds);
                }

                resp?.Dispose();
            }
        }
        finally
        {
            sem.Release();
        }
    }

    private static bool TryValidateTargetUri(string targetUri, out Uri uri)
    {
        if (Uri.TryCreate(targetUri, UriKind.Absolute, out var parsed) &&
            (string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private Task MarkRetryAsync(BackchannelLogoutNotification n, int attempt, Exception ex)
    {
        n.AttemptCount = attempt;
        n.Status = attempt < _options.MaxAttempts ? "pending" : "dead_letter";
        n.LastError = ex.Message;
        n.NextAttemptAt = DateTimeOffset.UtcNow.Add(ComputeBackoff(attempt));
        BumpCircuit(n.ClientId);
        return Task.CompletedTask;
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var ms = Math.Min(_options.MaxBackoff.TotalMilliseconds, Math.Pow(2, attempt) * _options.BaseBackoff.TotalMilliseconds);
        var jitter = Random.Shared.Next(0, 200);
        return TimeSpan.FromMilliseconds(ms + jitter);
    }

    private void BumpCircuit(string clientId)
    {
        if (!_circuits.TryGetValue(clientId, out var c))
        {
            c = new CircuitState();
            _circuits[clientId] = c;
        }
        c.Failures++;
        if (c.Failures >= _options.CircuitOpenFailures)
        {
            c.OpenUntil = DateTimeOffset.UtcNow.Add(_options.CircuitOpenDuration);
        }
        _state.Circuits[clientId] = new CircuitStateSnapshot(c.Failures, c.OpenUntil);
    }

    private void OpenCircuitMaybe(string clientId)
    {
        BumpCircuit(clientId);
    }

    private void ResetCircuit(string clientId)
    {
        if (_circuits.TryGetValue(clientId, out var c))
        {
            c.Failures = 0;
            c.OpenUntil = null;
        }
        _state.Circuits[clientId] = new CircuitStateSnapshot(0, null);
    }
}
