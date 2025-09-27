using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Pages.Admin.ProviderKeys;
using MrWhoOidc.WebAuth.Security;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class ProviderKeysPageModelTests
{
    private (AuthDbContext db, IndexModel model) Create()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<AuthDbContext>(o => o.UseInMemoryDatabase("pk-page-" + Guid.NewGuid().ToString("N")));
        services.AddLogging();
        services.AddMemoryCache();
        services.AddOptions();
        services.Configure<MrWhoOidc.Auth.Services.AuthOptions>(_ => { });
        services.AddOidcMetricsIfMissing();
        services.AddSingleton<MrWhoOidc.WebAuth.Observability.IOidcMetrics, MrWhoOidc.WebAuth.Observability.OidcMetrics>();
        services.AddScoped<IPublicJwksCache, PublicJwksCache>();
        var sp = services.BuildServiceProvider();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<AuthDbContext>>();
        var db = dbFactory.CreateDbContext();
        var cache = sp.GetRequiredService<IPublicJwksCache>();
        var model = new IndexModel(db, cache);
        return (db, model);
    }

    [TestMethod]
    public void InputModel_Default_Publishable_Is_True()
    {
        var (_, model) = Create();
        Assert.IsNotNull(model.Input, "Input model should be initialized");
        Assert.IsTrue(model.Input.Publishable, "Expected default Publishable=true on InputModel");
    }
}
