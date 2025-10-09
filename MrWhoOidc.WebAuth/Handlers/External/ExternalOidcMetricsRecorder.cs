using System.Diagnostics;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Centralizes metrics recording for external OIDC flows.
/// </summary>
public interface IExternalOidcMetricsRecorder
{
    void RecordStartRequest();
    void RecordStartOutcome(bool success, DateTime startTs, string? provider, string? clientId, string outcome);
    void RecordCallbackRequest();
    void RecordCallbackOutcome(bool success, DateTime startTs, string? provider, string? clientId, string outcome, bool? correlationPresent = null, bool? handleStale = null);
}

internal sealed class ExternalOidcMetricsRecorder : IExternalOidcMetricsRecorder
{
    private readonly IOidcMetrics _metrics;

    public ExternalOidcMetricsRecorder(IOidcMetrics metrics)
    {
        _metrics = metrics;
    }

    public void RecordStartRequest()
    {
        _metrics.ExternalStartRequests.Add(1);
    }

    public void RecordStartOutcome(bool success, DateTime startTs, string? provider, string? clientId, string outcome)
    {
        var tags = new TagList
        {
            { "provider", provider ?? string.Empty },
            { "clientId", clientId ?? string.Empty },
            { "outcome", outcome }
        };

        var durMs = (DateTime.UtcNow - startTs).TotalMilliseconds;
        _metrics.ExternalStartDurationMs.Record(durMs, tags);

        if (success)
            _metrics.ExternalStartSuccess.Add(1, tags);
        else
            _metrics.ExternalStartFailures.Add(1, tags);
    }

    public void RecordCallbackRequest()
    {
        _metrics.ExternalCallbackRequests.Add(1);
    }

    public void RecordCallbackOutcome(
        bool success,
        DateTime startTs,
        string? provider,
        string? clientId,
        string outcome,
        bool? correlationPresent = null,
        bool? handleStale = null)
    {
        var tags = new TagList
        {
            { "provider", provider ?? string.Empty },
            { "clientId", clientId ?? string.Empty },
            { "outcome", outcome },
            { "correlation", correlationPresent is null ? "unknown" : (correlationPresent.Value ? "present" : "missing") },
            { "handle", handleStale is null ? "unused" : (handleStale.Value ? "stale" : "fresh") }
        };

        var durMs = (DateTime.UtcNow - startTs).TotalMilliseconds;
        _metrics.ExternalCallbackDurationMs.Record(durMs, tags);

        if (success)
            _metrics.ExternalCallbackSuccess.Add(1, tags);
        else
            _metrics.ExternalCallbackFailures.Add(1, tags);

        _metrics.ExternalCallbackOutcomes.Add(1, tags);
    }
}
