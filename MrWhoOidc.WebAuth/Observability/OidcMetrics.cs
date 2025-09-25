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

    // Token Exchange specific metrics (RFC 8693)
    public Counter<long> TokenExchangeRequests { get; } = Meter.CreateCounter<long>("oidc.token_exchange.requests");
    public Counter<long> TokenExchangeSuccess { get; } = Meter.CreateCounter<long>("oidc.token_exchange.success");
    public Counter<long> TokenExchangeFailures { get; } = Meter.CreateCounter<long>("oidc.token_exchange.failures");
    public Histogram<double> TokenExchangeDurationMs { get; } = Meter.CreateHistogram<double>("oidc.token_exchange.duration.ms");

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

    // External OIDC (third-party identity) flow metrics
    public Counter<long> ExternalStartRequests { get; } = Meter.CreateCounter<long>("oidc.external.start.requests");
    public Counter<long> ExternalStartSuccess { get; } = Meter.CreateCounter<long>("oidc.external.start.success");
    public Counter<long> ExternalStartFailures { get; } = Meter.CreateCounter<long>("oidc.external.start.failures");
    public Histogram<double> ExternalStartDurationMs { get; } = Meter.CreateHistogram<double>("oidc.external.start.duration.ms");

    public Counter<long> ExternalCallbackRequests { get; } = Meter.CreateCounter<long>("oidc.external.callback.requests");
    public Counter<long> ExternalCallbackSuccess { get; } = Meter.CreateCounter<long>("oidc.external.callback.success");
    public Counter<long> ExternalCallbackFailures { get; } = Meter.CreateCounter<long>("oidc.external.callback.failures");
    public Histogram<double> ExternalCallbackDurationMs { get; } = Meter.CreateHistogram<double>("oidc.external.callback.duration.ms");

    // Back-channel logout metrics
    public Counter<long> BclEmitted { get; } = Meter.CreateCounter<long>("oidc.bcl.emitted");
    public Counter<long> BclDelivered { get; } = Meter.CreateCounter<long>("oidc.bcl.delivered");
    public Counter<long> BclFailed { get; } = Meter.CreateCounter<long>("oidc.bcl.failed");
    public Histogram<double> BclDeliveryLatencyMs { get; } = Meter.CreateHistogram<double>("oidc.bcl.delivery.ms");
    public ObservableGauge<long> BclPendingBacklog { get; }

    private long _backlog; // updated by dispatcher
    public void SetBclBacklog(long value) => _backlog = value;

    public OidcMetrics()
    {
        BclPendingBacklog = Meter.CreateObservableGauge<long>("oidc.bcl.backlog", () => _backlog);
    }
}
