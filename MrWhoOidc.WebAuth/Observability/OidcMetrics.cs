using System.Diagnostics.Metrics;

namespace MrWhoOidc.WebAuth.Observability;

public sealed class OidcMetrics
{
    public const string MeterName = "MrWhoOidc.WebAuth";
    private static readonly Meter Meter = new(MeterName);

    public Counter<long> AuthorizeRequests { get; } = Meter.CreateCounter<long>("oidc.authorize.requests");
    public Histogram<double> AuthorizeDurationMs { get; } = Meter.CreateHistogram<double>("oidc.authorize.duration.ms");

    public Counter<long> TokenRequests { get; } = Meter.CreateCounter<long>("oidc.token.requests");
    public Counter<long> TokenSuccess { get; } = Meter.CreateCounter<long>("oidc.token.success");
    public Counter<long> TokenFailures { get; } = Meter.CreateCounter<long>("oidc.token.failures");
    public Histogram<double> TokenDurationMs { get; } = Meter.CreateHistogram<double>("oidc.token.duration.ms");

    public Counter<long> UserInfoRequests { get; } = Meter.CreateCounter<long>("oidc.userinfo.requests");
    public Counter<long> UserInfoSuccess { get; } = Meter.CreateCounter<long>("oidc.userinfo.success");
    public Counter<long> UserInfoFailures { get; } = Meter.CreateCounter<long>("oidc.userinfo.failures");
    public Histogram<double> UserInfoDurationMs { get; } = Meter.CreateHistogram<double>("oidc.userinfo.duration.ms");

    public Counter<long> RevocationRequests { get; } = Meter.CreateCounter<long>("oidc.revocation.requests");
}
