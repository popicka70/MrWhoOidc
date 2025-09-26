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

namespace MrWhoOidc.UnitTests;

[TestClass]
public class ExternalOidcErrorTests
{
    private static (ExternalOidcHandler handler, DefaultHttpContext ctx) Create()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase("ext-err"));
        services.AddScoped<IClaimMappingService, ClaimMappingService>();
        services.AddSingleton(new OidcMetrics());
        services.AddHttpClient();
        services.AddSingleton<IJwksCache, JwksCache>();
        var sp = services.BuildServiceProvider();
        var db = sp.GetRequiredService<AuthDbContext>();
        var handler = new ExternalOidcHandler(db, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IDataProtectionProvider>(), sp.GetRequiredService<IJwksCache>(), sp.GetRequiredService<IClaimMappingService>(), sp.GetRequiredService<OidcMetrics>(), new NullLogger<ExternalOidcHandler>());
        var ctx = new DefaultHttpContext();
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
