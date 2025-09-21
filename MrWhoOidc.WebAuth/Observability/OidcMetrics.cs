using System.Diagnostics.Metrics;

namespace MrWhoOidc.WebAuth.Observability;

public sealed class OidcMetrics
{
    public const string MeterName = "MrWhoOidc.WebAuth";
    private static readonly Meter Meter = new(MeterName);

    public Counter<long> AuthorizeRequests { get; } = Meter.CreateCounter<long>("oidc.authorize.requests");
    public Histogram<double> AuthorizeDurationMs { get; } = Meter.CreateHistogram<double>("oidc.authorize.duration.ms");
    public Histogram<long> AuthorizeRequestSizeBytes { get; } = Meter.CreateHistogram<long>("oidc.authorize.request.size.bytes");

    public Counter<long> TokenRequests { get; } = Meter.CreateCounter<long>("oidc.token.requests");
    public Counter<long> TokenSuccess { get; } = Meter.CreateCounter<long>("oidc.token.success");
    public Counter<long> TokenFailures { get; } = Meter.CreateCounter<long>("oidc.token.failures");
    public Histogram<double> TokenDurationMs { get; } = Meter.CreateHistogram<double>("oidc.token.duration.ms");

    public Counter<long> UserInfoRequests { get; } = Meter.CreateCounter<long>("oidc.userinfo.requests");
    public Counter<long> UserInfoSuccess { get; } = Meter.CreateCounter<long>("oidc.userinfo.success");
    public Counter<long> UserInfoFailures { get; } = Meter.CreateCounter<long>("oidc.userinfo.failures");
    public Histogram<double> UserInfoDurationMs { get; } = Meter.CreateHistogram<double>("oidc.userinfo.duration.ms");

    public Counter<long> RevocationRequests { get; } = Meter.CreateCounter<long>("oidc.revocation.requests");

    // Introspection metrics
    public Counter<long> IntrospectionRequests { get; } = Meter.CreateCounter<long>("oidc.introspection.requests");
    public Counter<long> IntrospectionActiveTrue { get; } = Meter.CreateCounter<long>("oidc.introspection.active_true");
    public Counter<long> IntrospectionActiveFalse { get; } = Meter.CreateCounter<long>("oidc.introspection.active_false");
    public Histogram<double> IntrospectionDurationMs { get; } = Meter.CreateHistogram<double>("oidc.introspection.duration.ms");

    // PAR/JAR metrics
    public Counter<long> ParRequests { get; } = Meter.CreateCounter<long>("oidc.par.requests");
    public Counter<long> ParSuccess { get; } = Meter.CreateCounter<long>("oidc.par.success");
    public Counter<long> ParFailures { get; } = Meter.CreateCounter<long>("oidc.par.failures");
    public Counter<long> ParConsumed { get; } = Meter.CreateCounter<long>("oidc.par.consumed");
    public Histogram<long> ParRequestSizeBytes { get; } = Meter.CreateHistogram<long>("oidc.par.request.size.bytes");

    public Counter<long> JarValid { get; } = Meter.CreateCounter<long>("oidc.jar.valid");
    public Counter<long> JarInvalid { get; } = Meter.CreateCounter<long>("oidc.jar.invalid");
    public Histogram<long> JarRequestSizeBytes { get; } = Meter.CreateHistogram<long>("oidc.jar.request.size.bytes");
}
