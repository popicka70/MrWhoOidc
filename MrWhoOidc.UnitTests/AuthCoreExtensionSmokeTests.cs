using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class AuthCoreExtensionSmokeTests
{
    [TestMethod]
    public void AuthCore_Registers_Critical_Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase($"auth-core-smoke-{Guid.NewGuid():N}"));
        services.AddMrWhoOidcAuthCore();
        // Instead of resolving (which requires full EF setup), just verify descriptors are present.
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IKeyStore)), "IKeyStore descriptor missing");
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IPasswordHasher)), "IPasswordHasher descriptor missing");
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(ITokenService)), "ITokenService descriptor missing");
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(ITokenValidator)), "ITokenValidator descriptor missing");

        var capabilityCatalog = services.SingleOrDefault(d => d.ServiceType == typeof(IDelegableCapabilityCatalog));
        Assert.IsNotNull(capabilityCatalog, "IDelegableCapabilityCatalog descriptor missing");
        Assert.AreEqual(typeof(DelegableCapabilityCatalog), capabilityCatalog.ImplementationType);
        Assert.AreEqual(ServiceLifetime.Singleton, capabilityCatalog.Lifetime);

        var delegatedAuthorization = services.SingleOrDefault(d => d.ServiceType == typeof(IDelegatedAccessAuthorizationService));
        Assert.IsNotNull(delegatedAuthorization, "IDelegatedAccessAuthorizationService descriptor missing");
        Assert.AreEqual(typeof(DelegatedAccessAuthorizationService), delegatedAuthorization.ImplementationType);
        Assert.AreEqual(ServiceLifetime.Scoped, delegatedAuthorization.Lifetime);

        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IDelegatedAccessGrantService)), "IDelegatedAccessGrantService descriptor missing");

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsNotNull(scope.ServiceProvider.GetService<IDelegatedAccessAuthorizationService>());
    }

    [TestMethod]
    public void SecurityCore_StoreBackedJarCache_IsNotShadowedByLaterAuthCoreRegistration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DistributedState:Provider"] = "Memory"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddMrWhoOidcAuthCore(configuration);
        services.AddMrWhoOidcDistributedState(configuration);
        services.AddMrWhoOidcSecurityCore(configuration);
        // Program.cs calls AddMrWhoOidcPersistenceAndCore after the security core, and that calls
        // AddMrWhoOidcAuthCore again. The default in-memory cache must not replace the store-backed one.
        services.AddMrWhoOidcAuthCore(configuration);

        var descriptor = services.Last(d => d.ServiceType == typeof(IJarReplayCache));
        Assert.AreEqual(
            typeof(MrWhoOidc.WebAuth.Infrastructure.DistributedState.StoreJarReplayCache),
            descriptor.ImplementationType);

        // The same ordering hazard applies to the IDistributedCache adapter that HybridCache,
        // sessions and continuation stores consume.
        using var provider = services.BuildServiceProvider();
        Assert.IsInstanceOfType<DistributedStateCache>(
            provider.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>());
    }

    [TestMethod]
    public void PersistenceCore_Registers_Delegated_Access_Context()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Testing:UseInMemoryAuthDb"] = "true"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMrWhoOidcObservability(configuration);
        services.AddMrWhoOidcPersistenceAndCore(configuration);

        var contextService = services.SingleOrDefault(d => d.ServiceType == typeof(IDelegatedAccessContextService));
        Assert.IsNotNull(contextService, "IDelegatedAccessContextService descriptor missing");
        Assert.AreEqual(typeof(DelegatedAccessContextService), contextService.ImplementationType);
        Assert.AreEqual(ServiceLifetime.Scoped, contextService.Lifetime);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsNotNull(scope.ServiceProvider.GetService<IDelegatedAccessContextService>());
        Assert.IsInstanceOfType<MrWhoOidc.WebAuth.Observability.AuthAuditSinkAdapter>(
            scope.ServiceProvider.GetRequiredService<MrWhoOidc.Auth.Observability.IAuditSink>());
    }
}
