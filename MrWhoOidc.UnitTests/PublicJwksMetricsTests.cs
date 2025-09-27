using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Security;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.Auth.Services;

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
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OidcMetrics.MeterName && instrument.Name == "oidc.provider_jwks.zero_keys")
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
        services.AddLogging();
        services.AddOptions();
        services.Configure<AuthOptions>(_ => { });
        services.AddOidcMetricsIfMissing();
        services.AddSingleton<MrWhoOidc.WebAuth.Observability.IOidcMetrics, OidcMetrics>();
        services.AddScoped<IPublicJwksCache, PublicJwksCache>();
        var sp = services.BuildServiceProvider();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<AuthDbContext>>();
        var db = dbFactory.CreateDbContext();
        var cache = sp.GetRequiredService<IPublicJwksCache>();
        return (cache, db);
    }

    [TestMethod]
    public async Task ZeroKeysMetric_Increments_When_Active_NonPublishable_Key_Only()
    {
        using var capture = new MeterCapture();
    var (cache, db) = Create();
        var provider = new IdentityProvider { Name = "m1", Enabled = true };
        db.IdentityProviders.Add(provider);
        await db.SaveChangesAsync();
        db.IdentityProviderKeys.Add(new IdentityProviderKey { IdentityProviderId = provider.Id, Kid = "kid1", Alg = "RS256", Active = true, Publishable = false, Purpose = IdentityProviderKeyPurpose.Signing, Jwk = "{\"kty\":\"RSA\",\"n\":\"a\",\"e\":\"AQAB\",\"kid\":\"kid1\"}" });
        await db.SaveChangesAsync();
        var (_, json) = await cache.GetProviderAsync("m1", default);
        Assert.AreNotEqual("__not_found__", json);
        Assert.IsTrue(json.Contains("\"keys\":[]"));
        // Allow brief time for listener to process (listener callback is sync but publish path may be async flush)
        Assert.IsTrue(capture.ZeroKeys >= 1, $"Expected zero_keys metric >=1, got {capture.ZeroKeys}");
    }
}
