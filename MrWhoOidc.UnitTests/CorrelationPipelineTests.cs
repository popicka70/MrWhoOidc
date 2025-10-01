using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.UnitTests.TestSupport;

namespace MrWhoOidc.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class CorrelationPipelineTests
{
    [TestMethod]
    public async Task Middleware_InvalidHeader_GeneratesNewCorrelationId()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<RecordingOidcMetrics>();
        services.AddSingleton<IOidcMetrics>(sp => sp.GetRequiredService<RecordingOidcMetrics>());
        services.AddSingleton<ICorrelationIdGenerator, CorrelationIdGenerator>();
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddSingleton<ICorrelationStateCache>(sp =>
        {
            var memory = sp.GetRequiredService<IMemoryCache>();
            var metrics = sp.GetRequiredService<RecordingOidcMetrics>();
            var generator = sp.GetRequiredService<ICorrelationIdGenerator>();
            return new CorrelationStateCache(memory, redis: null, NullLogger<CorrelationStateCache>.Instance, metrics, generator);
        });

        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<ICorrelationContextAccessor>();
        var generator = provider.GetRequiredService<ICorrelationIdGenerator>();
        var cache = provider.GetRequiredService<ICorrelationStateCache>();
        var middleware = new CorrelationTrackingMiddleware(_ => Task.CompletedTask, accessor, generator, cache, NullLogger<CorrelationTrackingMiddleware>.Instance);

        var context = new DefaultHttpContext { RequestServices = provider };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        context.Request.Path = "/authorize";
        context.Request.Headers["X-Correlation-Id"] = "bad header value!";

        await middleware.InvokeAsync(context);

        Assert.IsTrue(accessor.HasCorrelation);
        var generated = accessor.CorrelationId;
        Assert.IsFalse(string.IsNullOrWhiteSpace(generated));
        Assert.AreNotEqual("bad header value!", generated, "Middleware should ignore invalid header values");
        Assert.AreEqual(generated, context.Response.Headers["X-Correlation-Id"].ToString());
    }

    [TestMethod]
    public async Task Callback_StaleHandle_EmitsCacheMissAndWriteMetrics()
    {
    using var scope = CreateServiceScope(out var handler, out var metrics);
        metrics.Reset();

        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("ext-oidc-state");
        var staleHandle = "ABCDEFGH"; // looks like a handle but never stored
        var state = BuildState(protector, staleHandle, correlationId: null);

        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = ctx;
        ctx.Request.QueryString = new QueryString("?state=" + Uri.EscapeDataString(state));

        var result = await handler.CallbackAsync(ctx);

        AssertRedirect(result, out var redirectUrl);
        StringAssert.StartsWith(redirectUrl, "/Auth/External/Error");

    Assert.AreEqual(1, metrics.GetCounterTotal("oidc.correlation.cache.misses"), "Cache miss should be recorded once");
    Assert.AreEqual(1, metrics.GetCounterEvents("oidc.correlation.cache.misses").Count, "Expected a single miss measurement");
    Assert.AreEqual(1, metrics.GetCounterTotal("oidc.correlation.cache.writes"), "New correlation handle should be stored once");
    Assert.AreEqual(1, metrics.GetCounterEvents("oidc.correlation.cache.writes").Count, "Expected a single write measurement");
        Assert.IsFalse(string.IsNullOrWhiteSpace(ctx.Response.Headers["X-Correlation-Id"].ToString()));
    }

    [TestMethod]
    public async Task Callback_InvalidHandleFormat_IgnoresHandleButStoresNewCorrelation()
    {
    using var scope = CreateServiceScope(out var handler, out var metrics);
        metrics.Reset();

        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("ext-oidc-state");
        var invalidHandle = "bad"; // fails LooksLikeHandle validation
        var state = BuildState(protector, invalidHandle, correlationId: null);

        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = ctx;
        ctx.Request.QueryString = new QueryString("?state=" + Uri.EscapeDataString(state));

        var result = await handler.CallbackAsync(ctx);

        AssertRedirect(result, out var redirectUrl);
        StringAssert.StartsWith(redirectUrl, "/Auth/External/Error");

    Assert.AreEqual(0, metrics.GetCounterTotal("oidc.correlation.cache.misses"), "Invalid handles should be ignored without cache lookup");
    Assert.AreEqual(0, metrics.GetCounterEvents("oidc.correlation.cache.misses").Count, "Invalid handles should not record miss measurements");
    Assert.AreEqual(1, metrics.GetCounterTotal("oidc.correlation.cache.writes"), "A single handle write was expected");
    Assert.AreEqual(1, metrics.GetCounterEvents("oidc.correlation.cache.writes").Count, "Expected one write measurement");
        Assert.IsFalse(string.IsNullOrWhiteSpace(scope.ServiceProvider.GetRequiredService<ICorrelationContextAccessor>().CorrelationId));
    }

    private static IServiceScope CreateServiceScope(out IExternalOidcHandler handler, out RecordingOidcMetrics metrics)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase("corr-tests" + Guid.NewGuid().ToString("N")));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<IClaimMappingService, ClaimMappingService>();
        services.AddSingleton<IJwksCache, JwksCache>();
        services.AddSingleton<RecordingOidcMetrics>();
        services.AddSingleton<IOidcMetrics>(sp => sp.GetRequiredService<RecordingOidcMetrics>());
        services.AddSingleton<ITokenMetricsRecorder, DefaultTokenMetricsRecorder>();
        services.AddScoped<IClientAssertionValidator, ClientAssertionValidator>();
        services.AddMrWhoOidcCorrelation(new ConfigurationBuilder().Build(), redisMux: null);
        services.AddExternalOidcHandler(); // Use DI registration

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();

        handler = scope.ServiceProvider.GetRequiredService<IExternalOidcHandler>();
        metrics = scope.ServiceProvider.GetRequiredService<RecordingOidcMetrics>();

        return new RootedScope(provider, scope);
    }

    private sealed class RootedScope : IServiceScope
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _inner;

        public RootedScope(ServiceProvider provider, IServiceScope inner)
        {
            _provider = provider;
            _inner = inner;
        }

        public IServiceProvider ServiceProvider => _inner.ServiceProvider;

        public void Dispose()
        {
            _inner.Dispose();
            _provider.Dispose();
        }
    }

    private static string BuildState(IDataProtector protector, string correlationHandle, string? correlationId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["Provider"] = "contoso",
            ["CodeVerifier"] = "code-verifier",
            ["ReturnUrl"] = "/authorize?client_id=web",
            ["Nonce"] = Guid.NewGuid().ToString("N"),
            ["ClientId"] = "web",
            ["cid_ref"] = correlationHandle,
            ["CorrelationId"] = correlationId,
            ["v"] = 1
        };
        var json = JsonSerializer.Serialize(payload);
        var protectedBytes = protector.Protect(Encoding.UTF8.GetBytes(json));
        return Base64Url(protectedBytes);
    }

    private static void AssertRedirect(IResult result, out string location)
    {
        var property = result.GetType().GetProperty("Url") ?? result.GetType().GetProperty("Location");
        Assert.IsNotNull(property, "Expected redirect result exposing Url/Location");
        location = property!.GetValue(result)?.ToString() ?? string.Empty;
        Assert.IsTrue(result is IResult, "Result should implement IResult");
        Assert.IsFalse(string.IsNullOrWhiteSpace(location), "Redirect location should be populated");
    }

    private static string Base64Url(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
