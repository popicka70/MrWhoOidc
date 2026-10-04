using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// R3: a suspended tenant kept resolving for 5 minutes because TenantService evicted "tenant:slug:{slug}" while the
/// request-path resolver cached under "tenant:{slug}".
/// </summary>
[TestClass]
public sealed class TenantResolverCacheTests
{
    [TestMethod]
    public async Task SuspendedTenant_StopsResolving_AfterInvalidation()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenant = new Tenant { Slug = "acme", Name = "Acme", IssuerUri = "https://idp/t/acme", Status = TenantStatus.Active };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        using var memory = new MemoryCache(new MemoryCacheOptions());
        var state = new MultiTenancyStateProvider("default", initialEnabled: true);
        var resolver = new ModeAwareTenantResolver(db, state, memory, NullLogger<ModeAwareTenantResolver>.Instance);
        var tenants = new TenantService(db, new TestHybridCache(), state, Options.Create(new TenantCacheOptions()), memory);

        Assert.IsNotNull(await resolver.ResolveTenantAsync("/t/acme/authorize"), "precondition: active tenant resolves and is cached");

        tenant.Status = TenantStatus.Suspended;
        await db.SaveChangesAsync();
        await tenants.InvalidateTenantCacheAsync(tenant.Id, tenant.Slug);

        Assert.IsNull(await resolver.ResolveTenantAsync("/t/acme/authorize"), "a suspended tenant must stop resolving once its cache entry is evicted");
    }
}
