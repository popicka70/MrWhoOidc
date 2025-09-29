using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MrWhoOidc.Client;

internal static class MrWhoOidcClientDefaults
{
    public const string DefaultSectionName = "MrWhoOidc:Client";
    public const string DefaultHttpClientName = "MrWhoOidcClient";
    public const string ActivitySourceName = "MrWhoOidc.Client";
    public const string MeterName = "MrWhoOidc.Client";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName, "0.1.0");

    public static readonly Histogram<double> TokenLatency = Meter.CreateHistogram<double>("mrwhooidc.client.token.latency", unit: "ms", description: "Latency for token endpoint calls");
    public static readonly Counter<long> TokenRequests = Meter.CreateCounter<long>("mrwhooidc.client.token.requests", description: "Number of token endpoint requests");
}
