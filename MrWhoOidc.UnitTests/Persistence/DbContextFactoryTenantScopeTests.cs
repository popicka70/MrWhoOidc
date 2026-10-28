using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.TestDoubles;

namespace MrWhoOidc.UnitTests.Persistence;

/// <summary>
/// H5: <c>AddDbContextFactory&lt;AuthDbContext&gt;()</c> activates the context against the root service
/// provider. The scoped <c>ITenantAccessor</c> was then resolved from the root: with scope validation on that
/// throws (so the optional client/provider JWKS cache misses and the backchannel alert sampler could not
/// activate), and with scope validation off every factory-created context shared one mutable root accessor.
/// </summary>
[TestClass]
public sealed class DbContextFactoryTenantScopeTests
{
    private static ServiceProvider BuildProvider(bool validateScopes = true)
    {
        var dbName = "factory-scope-" + Guid.NewGuid().ToString("N");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Testing:UseInMemoryAuthDb"] = "true",
                ["Testing:InMemoryDbName"] = dbName
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProtector>(new StubSecretProtector());
        services.AddScoped<ITenantAccessor, TenantAccessor>();
        services.AddAuthPersistence(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = validateScopes,
            ValidateOnBuild = false
        });
    }

    [TestMethod]
    public async Task Factory_CreatesAContext_WithScopeValidationEnabled()
    {
        using var provider = BuildProvider();

        // Resolving the factory and creating a context from the ROOT provider must not throw: the factory no
        // longer resolves the scoped ITenantAccessor from the root.
        var factory = provider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        Assert.IsNotNull(db);
    }

    [TestMethod]
    public async Task Factory_Contexts_DoNotShareOneAccessor()
    {
        using var provider = BuildProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<AuthDbContext>>();

        await using var first = factory.CreateDbContext();
        await using var second = factory.CreateDbContext();

        var field = typeof(AuthDbContext).GetField("_tenantAccessor", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "AuthDbContext must keep its accessor in a field for this assertion");

        var firstAccessor = field.GetValue(first);
        var secondAccessor = field.GetValue(second);

        Assert.IsNotNull(firstAccessor);
        Assert.IsNotNull(secondAccessor);
        Assert.IsFalse(ReferenceEquals(firstAccessor, secondAccessor),
            "two factory-created contexts must not share one accessor (a SetTenant on one would re-scope the other)");
    }

    [TestMethod]
    public async Task Factory_Context_FailsClosed_UntilACrossTenantScopeIsOpened()
    {
        using var provider = BuildProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<AuthDbContext>>();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using (var seed = factory.CreateDbContext())
        {
            using (TenantFilterScope.BeginSystemScope())
            {
                seed.Clients.Add(new ClientEntity { TenantId = tenantA, ClientId = "a" });
                seed.Clients.Add(new ClientEntity { TenantId = tenantB, ClientId = "b" });
                await seed.SaveChangesAsync();
            }
        }

        await using var reader = factory.CreateDbContext();

        Assert.AreEqual(0, await reader.Clients.CountAsync(),
            "a factory-created context has no tenant, so it must fail closed instead of seeing every tenant");
        Assert.AreEqual(0, await reader.Users.CountAsync());
        Assert.AreEqual(0, await reader.Realms.CountAsync());

        using (TenantFilterScope.BeginSystemScope())
        {
            Assert.AreEqual(2, await reader.Clients.CountAsync(),
                "an explicit cross-tenant scope is still required to read across tenants");
        }
    }
}
