using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.UnitTests.TestSupport;

internal sealed class RecordingOidcMetrics : IOidcMetrics, IDisposable
{
    private readonly Meter _meter;
    private readonly MeterListener _listener;
    private readonly object _gate = new();
    private readonly Dictionary<string, Counter<long>> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Histogram<double>> _doubleHistograms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Histogram<long>> _longHistograms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _counterTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<long>> _counterEvents = new(StringComparer.OrdinalIgnoreCase);
    private long _backlog;

    public RecordingOidcMetrics()
    {
        _meter = new Meter($"TestOidcMetrics-{Guid.NewGuid():N}");
        BclPendingBacklog = _meter.CreateObservableGauge<long>("oidc.bcl.backlog", () => _backlog);

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, _meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (!ReferenceEquals(instrument.Meter, _meter)) return;
            if (instrument is Counter<long>)
            {
                _counterTotals.AddOrUpdate(instrument.Name, measurement, (_, current) => current + measurement);
                var list = _counterEvents.GetOrAdd(instrument.Name, _ => new List<long>());
                lock (list)
                {
                    list.Add(measurement);
                }
            }
        });

        _listener.Start();
    }

    public long GetCounterTotal(string instrumentName)
        => _counterTotals.TryGetValue(instrumentName, out var value) ? value : 0;

    public IReadOnlyList<long> GetCounterEvents(string instrumentName)
    {
        if (_counterEvents.TryGetValue(instrumentName, out var list))
        {
            lock (list)
            {
                return list.ToArray();
            }
        }
        return Array.Empty<long>();
    }

    public void Reset()
    {
        _counterTotals.Clear();
        _counterEvents.Clear();
    }

    private Counter<long> Counter(string name)
    {
        lock (_gate)
        {
            if (!_counters.TryGetValue(name, out var counter))
            {
                counter = _meter.CreateCounter<long>(name);
                _counters[name] = counter;
            }
            return counter;
        }
    }

    private Histogram<double> HistogramDouble(string name)
    {
        lock (_gate)
        {
            if (!_doubleHistograms.TryGetValue(name, out var histogram))
            {
                histogram = _meter.CreateHistogram<double>(name);
                _doubleHistograms[name] = histogram;
            }
            return histogram;
        }
    }

    private Histogram<long> HistogramLong(string name)
    {
        lock (_gate)
        {
            if (!_longHistograms.TryGetValue(name, out var histogram))
            {
                histogram = _meter.CreateHistogram<long>(name);
                _longHistograms[name] = histogram;
            }
            return histogram;
        }
    }

    public Counter<long> AuthorizeRequests => Counter("oidc.authorize.requests");
    public Histogram<double> AuthorizeDurationMs => HistogramDouble("oidc.authorize.duration.ms");
    public Histogram<long> AuthorizeRequestSizeBytes => HistogramLong("oidc.authorize.request.size.bytes");
    public Counter<long> TokenRequests => Counter("oidc.token.requests");
    public Counter<long> TokenSuccess => Counter("oidc.token.success");
    public Counter<long> TokenFailures => Counter("oidc.token.failures");
    public Histogram<double> TokenDurationMs => HistogramDouble("oidc.token.duration.ms");
    public Counter<long> TokenExchangeRequests => Counter("oidc.token_exchange.requests");
    public Counter<long> TokenExchangeSuccess => Counter("oidc.token_exchange.success");
    public Counter<long> TokenExchangeFailures => Counter("oidc.token_exchange.failures");
    public Histogram<double> TokenExchangeDurationMs => HistogramDouble("oidc.token_exchange.duration.ms");
    public Counter<long> TokenExchangeRateLimitAllowed => Counter("oidc.token_exchange.ratelimit.allowed");
    public Counter<long> TokenExchangeRateLimitBlocked => Counter("oidc.token_exchange.ratelimit.blocked");
    public Counter<long> UserInfoRequests => Counter("oidc.userinfo.requests");
    public Counter<long> UserInfoSuccess => Counter("oidc.userinfo.success");
    public Counter<long> UserInfoFailures => Counter("oidc.userinfo.failures");
    public Histogram<double> UserInfoDurationMs => HistogramDouble("oidc.userinfo.duration.ms");
    public Counter<long> RevocationRequests => Counter("oidc.revocation.requests");
    public Counter<long> IntrospectionRequests => Counter("oidc.introspection.requests");
    public Counter<long> IntrospectionActiveTrue => Counter("oidc.introspection.active_true");
    public Counter<long> IntrospectionActiveFalse => Counter("oidc.introspection.active_false");
    public Histogram<double> IntrospectionDurationMs => HistogramDouble("oidc.introspection.duration.ms");
    public Counter<long> ParRequests => Counter("oidc.par.requests");
    public Counter<long> ParSuccess => Counter("oidc.par.success");
    public Counter<long> ParFailures => Counter("oidc.par.failures");
    public Counter<long> ParConsumed => Counter("oidc.par.consumed");
    public Histogram<long> ParRequestSizeBytes => HistogramLong("oidc.par.request.size.bytes");
    public Counter<long> JarValid => Counter("oidc.jar.valid");
    public Counter<long> JarInvalid => Counter("oidc.jar.invalid");
    public Histogram<long> JarRequestSizeBytes => HistogramLong("oidc.jar.request.size.bytes");
    public Counter<long> ExternalStartRequests => Counter("oidc.external.start.requests");
    public Counter<long> ExternalStartSuccess => Counter("oidc.external.start.success");
    public Counter<long> ExternalStartFailures => Counter("oidc.external.start.failures");
    public Histogram<double> ExternalStartDurationMs => HistogramDouble("oidc.external.start.duration.ms");
    public Counter<long> ExternalCallbackRequests => Counter("oidc.external.callback.requests");
    public Counter<long> ExternalCallbackSuccess => Counter("oidc.external.callback.success");
    public Counter<long> ExternalCallbackFailures => Counter("oidc.external.callback.failures");
    public Histogram<double> ExternalCallbackDurationMs => HistogramDouble("oidc.external.callback.duration.ms");
    public Counter<long> ExternalCallbackOutcomes => Counter("oidc.external.callback.outcomes");
    public Counter<long> BclEmitted => Counter("oidc.bcl.emitted");
    public Counter<long> BclDelivered => Counter("oidc.bcl.delivered");
    public Counter<long> BclFailed => Counter("oidc.bcl.failed");
    public Histogram<double> BclDeliveryLatencyMs => HistogramDouble("oidc.bcl.delivery.ms");
    public ObservableGauge<long> BclPendingBacklog { get; }
    public void SetBclBacklog(long value) => _backlog = value;
    public Counter<long> LogoutRequests => Counter("oidc.logout.requests");
    public Counter<long> LogoutFederated => Counter("oidc.logout.federated");
    public Counter<long> LogoutLocal => Counter("oidc.logout.local");
    public Counter<long> LogoutFailures => Counter("oidc.logout.failures");
    public Histogram<double> LogoutDuration => HistogramDouble("oidc.logout.duration.ms");
    public Counter<long> ProviderJwksRequests => Counter("oidc.provider_jwks.requests");
    public Counter<long> ProviderJwksAllRequests => Counter("oidc.provider_jwks.aggregated.requests");
    public Counter<long> ProviderJwksNotFound => Counter("oidc.provider_jwks.not_found");
    public Counter<long> ProviderJwksCacheHit => Counter("oidc.provider_jwks.cache.hit");
    public Counter<long> ProviderJwksCacheMiss => Counter("oidc.provider_jwks.cache.miss");
    public Counter<long> ProviderJwksKeysReturned => Counter("oidc.provider_jwks.keys.returned");
    public Counter<long> ProviderJwksZeroKeys => Counter("oidc.provider_jwks.zero_keys");
    public Counter<long> ProviderJwksEtagChanges => Counter("oidc.provider_jwks.etag_changes");
    public Counter<long> CorrelationCacheWrites => Counter("oidc.correlation.cache.writes");
    public Counter<long> CorrelationCacheHits => Counter("oidc.correlation.cache.hits");
    public Counter<long> CorrelationCacheMisses => Counter("oidc.correlation.cache.misses");
    public Counter<long> CorrelationCacheStale => Counter("oidc.correlation.cache.stale");

    public void Dispose()
    {
        _listener.Dispose();
        _meter.Dispose();
    }
}
