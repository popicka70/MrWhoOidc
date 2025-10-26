using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using MrWhoOidc.WebAuth.Handlers;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.UnitTests.TestDoubles;
using MrWhoOidc.WebAuth.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class ExternalOidcErrorTests
{
    private static (IExternalOidcHandler handler, DefaultHttpContext ctx) Create()
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
        services.AddMrWhoOidcCorrelation(new ConfigurationBuilder().Build(), redisMux: null);
        
        // Register ITenantAccessor for multi-tenant support
        services.AddScoped<ITenantAccessor>(_ => MockTenantAccessor.CreateWithDefaultTenant());
        
    services.AddSingleton<IEmailConfirmationWorkflow, FakeEmailConfirmationWorkflow>();
    services.AddExternalOidcHandler(); // Use DI registration
        var sp = services.BuildServiceProvider();
        var scope = sp.CreateScope();
        var scoped = scope.ServiceProvider;
        var handler = scoped.GetRequiredService<IExternalOidcHandler>();
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
        Assert.IsTrue(urlVal?.StartsWith("/Auth/External/Error", System.StringComparison.OrdinalIgnoreCase) ?? false, "Expected redirect to external error page");
    }
}
