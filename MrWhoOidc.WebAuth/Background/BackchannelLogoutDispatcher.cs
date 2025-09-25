using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
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

internal sealed class CircuitState
{
    public int Failures;
    public DateTimeOffset? OpenUntil;
}

public sealed class BackchannelLogoutDispatcher : BackgroundService
{
    private readonly IDbContextFactory<AuthDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BackchannelLogoutDispatcher> _logger;
    private readonly OidcMetrics _metrics;
    private readonly BackchannelDispatchOptions _options;
    private readonly Dictionary<string, CircuitState> _circuits = new(StringComparer.Ordinal);

    public BackchannelLogoutDispatcher(IDbContextFactory<AuthDbContext> dbFactory, IHttpClientFactory httpFactory, ILogger<BackchannelLogoutDispatcher> logger, OidcMetrics metrics, BackchannelDispatchOptions options)
    {
        _dbFactory = dbFactory;
        _httpFactory = httpFactory;
        _logger = logger;
        _metrics = metrics;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);
                var now = DateTimeOffset.UtcNow;
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
            using var db = await _dbFactory.CreateDbContextAsync(ct);
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

            var sw = System.Diagnostics.Stopwatch.StartNew();
            HttpResponseMessage? resp = null;
            Exception? lastEx = null;
            var attempt = n.AttemptCount + 1;

            try
            {
                using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("logout_token", n.LogoutToken) });
                resp = await http.PostAsync(n.TargetUri, content, ct);

                if (resp.IsSuccessStatusCode)
                {
                    n.Status = "succeeded";
                    _metrics.TokenSuccess.Add(1, new("kind", "bcl"), new("client_id", n.ClientId));
                    ResetCircuit(n.ClientId);
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
                    }
                    else
                    {
                        n.Status = attempt >= _options.MaxAttempts ? "dead_letter" : "failed";
                        OpenCircuitMaybe(n.ClientId);
                        _metrics.TokenFailures.Add(1, new("kind", "bcl"), new("client_id", n.ClientId));
                    }
                }
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                lastEx = ex;
                await MarkRetryAsync(n, attempt, ex);
            }
            catch (HttpRequestException ex)
            {
                lastEx = ex;
                await MarkRetryAsync(n, attempt, ex);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                n.Status = attempt >= _options.MaxAttempts ? "dead_letter" : "failed";
                n.AttemptCount = attempt;
                n.LastError = ex.Message;
                OpenCircuitMaybe(n.ClientId);
                _metrics.TokenFailures.Add(1, new("kind", "bcl"), new("client_id", n.ClientId));
            }
            finally
            {
                sw.Stop();
                n.LastAttemptAt = DateTimeOffset.UtcNow;
                _metrics.TokenDurationMs.Record(sw.Elapsed.TotalMilliseconds, new("kind", "bcl"), new("client_id", n.ClientId));
                await db.SaveChangesAsync(ct);
                if (lastEx != null)
                {
                    _logger.LogWarning(lastEx, "BCL delivery error for {ClientId} attempt {Attempt}", n.ClientId, attempt);
                }
                else if (resp != null && !resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("BCL delivery failed for {ClientId} HTTP {Status} attempt {Attempt}", n.ClientId, (int)resp.StatusCode, attempt);
                }
                else if (resp != null)
                {
                    _logger.LogInformation("BCL delivery succeeded for {ClientId} in {Ms}ms", n.ClientId, sw.ElapsedMilliseconds);
                }
                sem.Release();
            }
        }
        catch
        {
            sem.Release();
            throw;
        }
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
    }
}
