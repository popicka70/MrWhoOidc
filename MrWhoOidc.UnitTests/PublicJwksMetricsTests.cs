using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.UnitTests.TestDoubles;
using MrWhoOidc.WebAuth.Security;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class PublicJwksMetricsTests
{
    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener;
        public long ZeroKeys { get; private set; }
        public MeterCapture()
        {
            _listener = new MeterListener();
            // Signature in current target: Action<Instrument, MeterListener>
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OidcEndpointMetrics.MeterName && instrument.Name == "oidc.provider_jwks.zero_keys")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            {
                if (instrument.Name == "oidc.provider_jwks.zero_keys")
                {
                    ZeroKeys += value;
                }
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }

    private (IPublicJwksCache cache, AuthDbContext db) Create()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<AuthDbContext>(o => o.UseInMemoryDatabase("jwks-metrics-" + Guid.NewGuid().ToString("N")));
        services.AddMemoryCache();
        services.AddHybridCache(); // Required for PublicJwksCache
        services.AddLogging();
        services.AddOptions();
        services.Configure<AuthOptions>(_ => { });
        services.AddOidcMetricsIfMissing();
        services.AddSingleton<MrWhoOidc.WebAuth.Observability.IOidcMetrics, OidcEndpointMetrics>();
        services.AddSingleton<ITenantAccessor>(new MockTenantAccessor());
        services.AddSingleton<ISecretProtector>(_ => new StubSecretProtector());
        services.AddScoped<IPublicJwksCache, PublicJwksCache>();
        var sp = services.BuildServiceProvider();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<AuthDbContext>>();
        var db = dbFactory.CreateDbContext();
        var cache = sp.GetRequiredService<IPublicJwksCache>();
        return (cache, db);
    }

    [TestMethod]
    public void ZeroKeysMetric_Increments_When_Active_NonPublishable_Key_Only()
    {
        using var capture = new MeterCapture();
        var (cache, db) = Create();
        var provider = new IdentityProvider { Name = "m1", Enabled = true };
        db.IdentityProviders.Add(provider);
        db.SaveChanges();
        db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = provider.Id, Kid = "kid1", Alg = "RS256", Active = true, Publishable = false, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"a\",\"e\":\"AQAB\",\"kid\":\"kid1\"}" });
        db.SaveChanges();
        var task = cache.GetProviderAsync("m1", default);
        task.GetAwaiter().GetResult();
        var (_, json) = task.Result;
        Assert.AreNotEqual("__not_found__", json);
        Assert.Contains("\"keys\":[]", json);
        Assert.IsGreaterThanOrEqualTo(1, capture.ZeroKeys, $"Expected zero_keys metric >=1, got {capture.ZeroKeys}");
    }
}
