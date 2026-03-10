using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Observability;

public sealed class AuditOptions
{
    // Feature flag to enable/disable audit emission
    public bool Enabled { get; set; } = true;
    // Optional pepper for hashing PII like sid/sub
    public string? Pepper { get; set; }
    // Sink: logger (default) | appinsights (if telemetry configured) | both
    public string Sink { get; set; } = "logger";
    // When using Application Insights, allow overriding the telemetry name prefix
    public string AppInsightsEventName { get; set; } = "audit";
    // Persist events to the AuthDbContext audit table.
    public bool PersistToDatabase { get; set; } = true;
    // Max serialized payload size to store in database.
    public int MaxStoredPayloadBytes { get; set; } = 16 * 1024;
}

public interface IAuditSink
{
    void Emit(string type, object payload);
    string? HashValue(string? value);
}

public sealed class NoopAuditSink : IAuditSink
{
    public void Emit(string type, object payload) { }
    public string? HashValue(string? value) => null;
}

public sealed class LoggerAuditSink(ILogger<LoggerAuditSink> logger, Microsoft.Extensions.Options.IOptions<AuditOptions> options) : IAuditSink
{
    private readonly ILogger<LoggerAuditSink> _logger = logger;
    private readonly string? _pepper = options.Value.Pepper;

    public void Emit(string type, object payload)
    {
        // Single, consistent audit log line with structured properties
        // Avoid logging any raw JWT or secrets in payload objects
        _logger.LogInformation("audit {Type} {@Event}", type, payload);
    }

    public string? HashValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var input = string.IsNullOrEmpty(_pepper) ? value : ($"{_pepper}:{value}");
            return MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Hex(input);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class ApplicationInsightsAuditSink(
    Microsoft.ApplicationInsights.TelemetryClient telemetry,
    ILogger<ApplicationInsightsAuditSink> logger,
    Microsoft.Extensions.Options.IOptions<AuditOptions> options) : IAuditSink
{
    private readonly Microsoft.ApplicationInsights.TelemetryClient _telemetry = telemetry;
    private readonly ILogger<ApplicationInsightsAuditSink> _logger = logger;
    private readonly string? _pepper = options.Value.Pepper;
    private readonly string _eventName = string.IsNullOrWhiteSpace(options.Value.AppInsightsEventName) ? "audit" : options.Value.AppInsightsEventName.Trim();

    public void Emit(string type, object payload)
    {
        try
        {
            // Convert anonymous payload object to dictionary via reflection (shallow) to attach as custom properties
            var props = new Dictionary<string, string?>
            {
                ["type"] = type,
            };

            foreach (var prop in payload.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                var name = prop.Name;
                object? val;
                try { val = prop.GetValue(payload); } catch { continue; }
                if (val is null) continue;
                var str = val switch
                {
                    string s => s,
                    Guid g => g.ToString(),
                    Uri u => u.ToString(),
                    DateTime dt => dt.ToUniversalTime().ToString("O"),
                    DateTimeOffset dto => dto.ToUniversalTime().ToString("O"),
                    _ => val.ToString()
                };
                if (string.IsNullOrWhiteSpace(str)) continue;
                // Avoid overly large values
                if (str.Length > 2048) str = str.Substring(0, 2048);
                props[name] = str;
            }

            _telemetry.TrackEvent(_eventName, props!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit audit event to Application Insights");
        }
    }

    public string? HashValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var input = string.IsNullOrEmpty(_pepper) ? value : ($"{_pepper}:{value}");
            return MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Hex(input);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class CompositeAuditSink(IEnumerable<IAuditSink> sinks) : IAuditSink
{
    private readonly IReadOnlyList<IAuditSink> _sinks = sinks.ToList();
    public void Emit(string type, object payload)
    {
        foreach (var s in _sinks)
        {
            try { s.Emit(type, payload); } catch { /* swallow */ }
        }
    }
    public string? HashValue(string? value)
    {
        // Use first sink's hashing behavior (they all should be consistent)
        var first = _sinks.FirstOrDefault();
        return first?.HashValue(value);
    }
}

public sealed class DbAuditSink(
    IServiceScopeFactory scopeFactory,
    ILogger<DbAuditSink> logger,
    Microsoft.Extensions.Options.IOptions<AuditOptions> options,
    IHttpContextAccessor httpContextAccessor) : IAuditSink
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<DbAuditSink> _logger = logger;
    private readonly string? _pepper = options.Value.Pepper;
    private readonly int _maxPayloadBytes = options.Value.MaxStoredPayloadBytes > 0 ? options.Value.MaxStoredPayloadBytes : 16 * 1024;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    public void Emit(string type, object payload)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        try
        {
            var payloadJson = JsonSerializer.Serialize(payload);
            if (_maxPayloadBytes > 0 && Encoding.UTF8.GetByteCount(payloadJson) > _maxPayloadBytes)
            {
                payloadJson = payloadJson[..Math.Min(payloadJson.Length, 4096)];
            }

            Guid? tenantId = null;
            string? traceId = Activity.Current?.Id;
            string? actorHash = null;
            string? ipHash = null;

            var http = _httpContextAccessor.HttpContext;
            if (http != null)
            {
                traceId ??= http.TraceIdentifier;

                var tenantAccessor = http.RequestServices.GetService<ITenantAccessor>();
                tenantId = tenantAccessor?.CurrentTenant?.TenantId;

                var actor = http.User?.FindFirstValue("sub")
                    ?? http.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? http.User?.Identity?.Name;

                actorHash = HashValue(actor);
                ipHash = HashValue(http.Connection.RemoteIpAddress?.ToString());
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                EventType = type,
                PayloadJson = payloadJson,
                OccurredAt = DateTimeOffset.UtcNow,
                TraceId = traceId,
                ActorHash = actorHash,
                IpHash = ipHash
            });

            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist audit event '{Type}'", type);
        }
    }

    public string? HashValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var input = string.IsNullOrEmpty(_pepper) ? value : ($"{_pepper}:{value}");
            return MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Hex(input);
        }
        catch
        {
            return null;
        }
    }
}

