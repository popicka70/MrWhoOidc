using System.Diagnostics;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Records metrics for introspection operations.
/// </summary>
internal sealed class IntrospectionMetrics(OidcMetrics metrics)
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public void RecordRequest(KeyValuePair<string, object?>[] tags)
    {
        metrics.IntrospectionRequests.Add(1, tags);
    }

    public void RecordActiveTrue(KeyValuePair<string, object?>[] tags)
    {
        metrics.IntrospectionActiveTrue.Add(1, tags);
        metrics.IntrospectionDurationMs.Record(_stopwatch.Elapsed.TotalMilliseconds, tags);
    }

    public void RecordActiveFalse(KeyValuePair<string, object?>[] tags)
    {
        metrics.IntrospectionActiveFalse.Add(1, tags);
        metrics.IntrospectionDurationMs.Record(_stopwatch.Elapsed.TotalMilliseconds, tags);
    }
}
