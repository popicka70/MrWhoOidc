using System.Diagnostics.Metrics;

namespace MrWhoOidc.WebAuth.Observability;

public interface IOidcMetrics
{
    Counter<long> AuthorizeRequests { get; }
    Histogram<double> AuthorizeDurationMs { get; }
    Histogram<long> AuthorizeRequestSizeBytes { get; }
    Counter<long> TokenRequests { get; }
    Counter<long> TokenSuccess { get; }
    Counter<long> TokenFailures { get; }
    Histogram<double> TokenDurationMs { get; }
    Counter<long> TokenExchangeRequests { get; }
    Counter<long> TokenExchangeSuccess { get; }
    Counter<long> TokenExchangeFailures { get; }
    Histogram<double> TokenExchangeDurationMs { get; }
    Counter<long> TokenExchangeRateLimitAllowed { get; }
    Counter<long> TokenExchangeRateLimitBlocked { get; }
    Counter<long> UserInfoRequests { get; }
    Counter<long> UserInfoSuccess { get; }
    Counter<long> UserInfoFailures { get; }
    Histogram<double> UserInfoDurationMs { get; }
    Counter<long> RevocationRequests { get; }
    Counter<long> IntrospectionRequests { get; }
    Counter<long> IntrospectionActiveTrue { get; }
    Counter<long> IntrospectionActiveFalse { get; }
    Histogram<double> IntrospectionDurationMs { get; }
    Counter<long> ParRequests { get; }
    Counter<long> ParSuccess { get; }
    Counter<long> ParFailures { get; }
    Counter<long> ParConsumed { get; }
    Histogram<long> ParRequestSizeBytes { get; }
    Counter<long> JarValid { get; }
    Counter<long> JarInvalid { get; }
    Histogram<long> JarRequestSizeBytes { get; }
    Counter<long> ExternalStartRequests { get; }
    Counter<long> ExternalStartSuccess { get; }
    Counter<long> ExternalStartFailures { get; }
    Histogram<double> ExternalStartDurationMs { get; }
    Counter<long> ExternalCallbackRequests { get; }
    Counter<long> ExternalCallbackSuccess { get; }
    Counter<long> ExternalCallbackFailures { get; }
    Histogram<double> ExternalCallbackDurationMs { get; }
    Counter<long> ExternalCallbackOutcomes { get; }
    Counter<long> BclEmitted { get; }
    Counter<long> BclDelivered { get; }
    Counter<long> BclFailed { get; }
    Histogram<double> BclDeliveryLatencyMs { get; }
    ObservableGauge<long> BclPendingBacklog { get; }
    void SetBclBacklog(long value);
    Counter<long> LogoutRequests { get; }
    Counter<long> LogoutFederated { get; }
    Counter<long> LogoutLocal { get; }
    Counter<long> LogoutFailures { get; }
    Histogram<double> LogoutDuration { get; }
    Counter<long> ProviderJwksRequests { get; }
    Counter<long> ProviderJwksAllRequests { get; }
    Counter<long> ProviderJwksNotFound { get; }
    Counter<long> ProviderJwksCacheHit { get; }
    Counter<long> ProviderJwksCacheMiss { get; }
    Counter<long> ProviderJwksKeysReturned { get; }
    Counter<long> ProviderJwksZeroKeys { get; }
    Counter<long> ProviderJwksEtagChanges { get; }
    Counter<long> CorrelationCacheWrites { get; }
    Counter<long> CorrelationCacheHits { get; }
    Counter<long> CorrelationCacheMisses { get; }
    Counter<long> CorrelationCacheStale { get; }
}

public sealed class OidcEndpointMetrics : IOidcMetrics
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
    public Counter<long> TokenExchangeRateLimitAllowed { get; } = Meter.CreateCounter<long>("oidc.token_exchange.ratelimit.allowed");
    public Counter<long> TokenExchangeRateLimitBlocked { get; } = Meter.CreateCounter<long>("oidc.token_exchange.ratelimit.blocked");

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
    public Counter<long> ExternalCallbackOutcomes { get; } = Meter.CreateCounter<long>("oidc.external.callback.outcomes");

    // Back-channel logout metrics
    public Counter<long> BclEmitted { get; } = Meter.CreateCounter<long>("oidc.bcl.emitted");
    public Counter<long> BclDelivered { get; } = Meter.CreateCounter<long>("oidc.bcl.delivered");
    public Counter<long> BclFailed { get; } = Meter.CreateCounter<long>("oidc.bcl.failed");
    public Histogram<double> BclDeliveryLatencyMs { get; } = Meter.CreateHistogram<double>("oidc.bcl.delivery.ms");
    public ObservableGauge<long> BclPendingBacklog { get; }

    private long _backlog; // updated by dispatcher
    public void SetBclBacklog(long value) => _backlog = value;

    public OidcEndpointMetrics()
    {
        BclPendingBacklog = Meter.CreateObservableGauge<long>("oidc.bcl.backlog", () => _backlog);
    }

    // Federated logout metrics (added later to avoid breaking consumers)
    public Counter<long> LogoutRequests { get; } = Meter.CreateCounter<long>("oidc.logout.requests");
    public Counter<long> LogoutFederated { get; } = Meter.CreateCounter<long>("oidc.logout.federated");
    public Counter<long> LogoutLocal { get; } = Meter.CreateCounter<long>("oidc.logout.local");
    public Counter<long> LogoutFailures { get; } = Meter.CreateCounter<long>("oidc.logout.failures");
    public Histogram<double> LogoutDuration { get; } = Meter.CreateHistogram<double>("oidc.logout.duration.ms");

    // Provider JWKS metrics
    public Counter<long> ProviderJwksRequests { get; } = Meter.CreateCounter<long>("oidc.provider_jwks.requests");
    public Counter<long> ProviderJwksAllRequests { get; } = Meter.CreateCounter<long>("oidc.provider_jwks.aggregated.requests");
    public Counter<long> ProviderJwksNotFound { get; } = Meter.CreateCounter<long>("oidc.provider_jwks.not_found");
    public Counter<long> ProviderJwksCacheHit { get; } = Meter.CreateCounter<long>("oidc.provider_jwks.cache.hit");
    public Counter<long> ProviderJwksCacheMiss { get; } = Meter.CreateCounter<long>("oidc.provider_jwks.cache.miss");
    public Counter<long> ProviderJwksKeysReturned { get; } = Meter.CreateCounter<long>("oidc.provider_jwks.keys.returned");
    public Counter<long> ProviderJwksZeroKeys { get; } = Meter.CreateCounter<long>("oidc.provider_jwks.zero_keys");
    public Counter<long> ProviderJwksEtagChanges { get; } = Meter.CreateCounter<long>("oidc.provider_jwks.etag_changes");
    public Counter<long> CorrelationCacheWrites { get; } = Meter.CreateCounter<long>("oidc.correlation.cache.writes");
    public Counter<long> CorrelationCacheHits { get; } = Meter.CreateCounter<long>("oidc.correlation.cache.hits");
    public Counter<long> CorrelationCacheMisses { get; } = Meter.CreateCounter<long>("oidc.correlation.cache.misses");
    public Counter<long> CorrelationCacheStale { get; } = Meter.CreateCounter<long>("oidc.correlation.cache.stale");
}

internal sealed class NoOpOidcMetrics : IOidcMetrics
{
    private static readonly Meter Meter = new("MrWhoOidc.WebAuth.noop");
    private static Counter<long> C(string name) => Meter.CreateCounter<long>(name + ".noop");
    private static Histogram<double> H(string name) => Meter.CreateHistogram<double>(name + ".noop");
    private static Histogram<long> HL(string name) => Meter.CreateHistogram<long>(name + ".noop");
    private static readonly ObservableGauge<long> G = Meter.CreateObservableGauge<long>("oidc.noop.g", () => 0L);
    public Counter<long> AuthorizeRequests { get; } = C("oidc.authorize.requests");
    public Histogram<double> AuthorizeDurationMs { get; } = H("oidc.authorize.duration.ms");
    public Histogram<long> AuthorizeRequestSizeBytes { get; } = HL("oidc.authorize.request.size.bytes");
    public Counter<long> TokenRequests { get; } = C("oidc.token.requests");
    public Counter<long> TokenSuccess { get; } = C("oidc.token.success");
    public Counter<long> TokenFailures { get; } = C("oidc.token.failures");
    public Histogram<double> TokenDurationMs { get; } = H("oidc.token.duration.ms");
    public Counter<long> TokenExchangeRequests { get; } = C("oidc.token_exchange.requests");
    public Counter<long> TokenExchangeSuccess { get; } = C("oidc.token_exchange.success");
    public Counter<long> TokenExchangeFailures { get; } = C("oidc.token_exchange.failures");
    public Histogram<double> TokenExchangeDurationMs { get; } = H("oidc.token_exchange.duration.ms");
    public Counter<long> TokenExchangeRateLimitAllowed { get; } = C("oidc.token_exchange.ratelimit.allowed");
    public Counter<long> TokenExchangeRateLimitBlocked { get; } = C("oidc.token_exchange.ratelimit.blocked");
    public Counter<long> UserInfoRequests { get; } = C("oidc.userinfo.requests");
    public Counter<long> UserInfoSuccess { get; } = C("oidc.userinfo.success");
    public Counter<long> UserInfoFailures { get; } = C("oidc.userinfo.failures");
    public Histogram<double> UserInfoDurationMs { get; } = H("oidc.userinfo.duration.ms");
    public Counter<long> RevocationRequests { get; } = C("oidc.revocation.requests");
    public Counter<long> IntrospectionRequests { get; } = C("oidc.introspection.requests");
    public Counter<long> IntrospectionActiveTrue { get; } = C("oidc.introspection.active_true");
    public Counter<long> IntrospectionActiveFalse { get; } = C("oidc.introspection.active_false");
    public Histogram<double> IntrospectionDurationMs { get; } = H("oidc.introspection.duration.ms");
    public Counter<long> ParRequests { get; } = C("oidc.par.requests");
    public Counter<long> ParSuccess { get; } = C("oidc.par.success");
    public Counter<long> ParFailures { get; } = C("oidc.par.failures");
    public Counter<long> ParConsumed { get; } = C("oidc.par.consumed");
    public Histogram<long> ParRequestSizeBytes { get; } = HL("oidc.par.request.size.bytes");
    public Counter<long> JarValid { get; } = C("oidc.jar.valid");
    public Counter<long> JarInvalid { get; } = C("oidc.jar.invalid");
    public Histogram<long> JarRequestSizeBytes { get; } = HL("oidc.jar.request.size.bytes");
    public Counter<long> ExternalStartRequests { get; } = C("oidc.external.start.requests");
    public Counter<long> ExternalStartSuccess { get; } = C("oidc.external.start.success");
    public Counter<long> ExternalStartFailures { get; } = C("oidc.external.start.failures");
    public Histogram<double> ExternalStartDurationMs { get; } = H("oidc.external.start.duration.ms");
    public Counter<long> ExternalCallbackRequests { get; } = C("oidc.external.callback.requests");
    public Counter<long> ExternalCallbackSuccess { get; } = C("oidc.external.callback.success");
    public Counter<long> ExternalCallbackFailures { get; } = C("oidc.external.callback.failures");
    public Histogram<double> ExternalCallbackDurationMs { get; } = H("oidc.external.callback.duration.ms");
    public Counter<long> ExternalCallbackOutcomes { get; } = C("oidc.external.callback.outcomes");
    public Counter<long> BclEmitted { get; } = C("oidc.bcl.emitted");
    public Counter<long> BclDelivered { get; } = C("oidc.bcl.delivered");
    public Counter<long> BclFailed { get; } = C("oidc.bcl.failed");
    public Histogram<double> BclDeliveryLatencyMs { get; } = H("oidc.bcl.delivery.ms");
    public ObservableGauge<long> BclPendingBacklog { get; } = G;
    public void SetBclBacklog(long value) { }
    public Counter<long> LogoutRequests { get; } = C("oidc.logout.requests");
    public Counter<long> LogoutFederated { get; } = C("oidc.logout.federated");
    public Counter<long> LogoutLocal { get; } = C("oidc.logout.local");
    public Counter<long> LogoutFailures { get; } = C("oidc.logout.failures");
    public Histogram<double> LogoutDuration { get; } = H("oidc.logout.duration.ms");
    public Counter<long> ProviderJwksRequests { get; } = C("oidc.provider_jwks.requests");
    public Counter<long> ProviderJwksAllRequests { get; } = C("oidc.provider_jwks.aggregated.requests");
    public Counter<long> ProviderJwksNotFound { get; } = C("oidc.provider_jwks.not_found");
    public Counter<long> ProviderJwksCacheHit { get; } = C("oidc.provider_jwks.cache.hit");
    public Counter<long> ProviderJwksCacheMiss { get; } = C("oidc.provider_jwks.cache.miss");
    public Counter<long> ProviderJwksKeysReturned { get; } = C("oidc.provider_jwks.keys.returned");
    public Counter<long> ProviderJwksZeroKeys { get; } = C("oidc.provider_jwks.zero_keys");
    public Counter<long> ProviderJwksEtagChanges { get; } = C("oidc.provider_jwks.etag_changes");
    public Counter<long> CorrelationCacheWrites { get; } = C("oidc.correlation.cache.writes");
    public Counter<long> CorrelationCacheHits { get; } = C("oidc.correlation.cache.hits");
    public Counter<long> CorrelationCacheMisses { get; } = C("oidc.correlation.cache.misses");
    public Counter<long> CorrelationCacheStale { get; } = C("oidc.correlation.cache.stale");
}
