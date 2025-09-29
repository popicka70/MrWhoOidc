using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using MrWhoOidc.WebAuth.Handlers;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class ExternalOidcErrorTests
{
    private static (ExternalOidcHandler handler, DefaultHttpContext ctx) Create()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddMemoryCache();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase("ext-err"));
        services.AddScoped<IClaimMappingService, ClaimMappingService>();
        services.AddSingleton<OidcMetrics>();
        services.AddSingleton<IOidcMetrics>(sp => sp.GetRequiredService<OidcMetrics>());
        services.AddSingleton<IOptions<AuthOptions>>(Options.Create(new AuthOptions()));
        services.AddHttpClient();
        services.AddSingleton<IJwksCache, JwksCache>();
        services.AddHttpContextAccessor();
        services.AddSingleton<ICorrelationIdGenerator, CorrelationIdGenerator>();
        services.AddScoped<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddSingleton<ICorrelationStateCache>(sp =>
        {
            var memory = sp.GetRequiredService<IMemoryCache>();
            var metrics = sp.GetRequiredService<IOidcMetrics>();
            var generator = sp.GetRequiredService<ICorrelationIdGenerator>();
            return new CorrelationStateCache(memory, null, NullLogger<CorrelationStateCache>.Instance, metrics, generator);
        });
        var sp = services.BuildServiceProvider();
        var scope = sp.CreateScope();
        var scoped = scope.ServiceProvider;
        var db = scoped.GetRequiredService<AuthDbContext>();
        var handler = new ExternalOidcHandler(
            db,
            scoped.GetRequiredService<IHttpClientFactory>(),
            scoped.GetRequiredService<IDataProtectionProvider>(),
            scoped.GetRequiredService<IJwksCache>(),
            scoped.GetRequiredService<IClaimMappingService>(),
            scoped.GetRequiredService<ICorrelationContextAccessor>(),
            scoped.GetRequiredService<ICorrelationStateCache>(),
            scoped.GetRequiredService<ICorrelationIdGenerator>(),
            scoped.GetRequiredService<OidcMetrics>(),
            new NullLogger<ExternalOidcHandler>());
        var ctx = new DefaultHttpContext
        {
            RequestServices = scoped
        };
        scoped.GetRequiredService<IHttpContextAccessor>().HttpContext = ctx;
        ctx.Items["__scope"] = scope; // keep scope alive for test duration
        return (handler, ctx);
    }

    [TestMethod]
    public async Task Start_UnknownProvider_ReturnsRedirectToErrorPage()
    {
        var (h, ctx) = Create();
        ctx.Request.QueryString = new QueryString("?provider=doesnotexist&returnUrl=%2Fauthorize%3Fclient_id%3Dweb&clientId=web");
        var result = await h.StartAsync(ctx);
        Assert.IsNotNull(result);
        // The new FriendlyError returns a redirect to /Auth/External/Error
        var prop = result.GetType().GetProperty("Url") ?? result.GetType().GetProperty("Location");
        var urlVal = prop?.GetValue(result)?.ToString();
        Assert.IsTrue(urlVal?.StartsWith("/Auth/External/Error", System.StringComparison.OrdinalIgnoreCase) == true, "Expected redirect to external error page");
    }
}
