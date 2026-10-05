using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Middleware;

namespace MrWhoOidc.UnitTests.MultiTenancy;

/// <summary>
/// Assessment 2026-10-04 D17: the EF tenant query filter failed OPEN - a query issued without a tenant saw every
/// tenant's rows. It now fails closed; cross-tenant access needs an explicit system scope (or IgnoreQueryFilters),
/// and MultiTenancy:TenantFilterFailOpen is an operator escape hatch restoring the legacy behaviour.
/// </summary>
[TestClass]
public sealed class TenantFilterFailClosedTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private static (AuthDbContext Db, TenantAccessor Accessor) CreateDb(string dbName, bool failOpen = false)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options;
        var accessor = new TenantAccessor();
        var db = new AuthDbContext(options, accessor, null, new TenantFilterOptions { FailOpen = failOpen });
        return (db, accessor);
    }

    private static async Task<string> SeedAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options;
        await using var seed = new AuthDbContext(options); // no accessor: unscoped by construction
        var clientA = new MrWhoOidc.Auth.Persistence.Client { TenantId = TenantA, ClientId = "a" };
        var clientB = new MrWhoOidc.Auth.Persistence.Client { TenantId = TenantB, ClientId = "b" };
        seed.Clients.AddRange(clientA, clientB);
        seed.Users.AddRange(
            new User { TenantId = TenantA, Username = "alice" },
            new User { TenantId = TenantB, Username = "bob" });
        seed.Scopes.AddRange(
            new Scope { Name = "openid", TenantId = null, IsGlobal = true },
            new Scope { Name = "a.scope", TenantId = TenantA },
            new Scope { Name = "b.scope", TenantId = TenantB });
        seed.ClientScopes.AddRange(
            new ClientScope { ClientId = clientA.Id, ScopeName = "openid" },
            new ClientScope { ClientId = clientB.Id, ScopeName = "openid" });
        await seed.SaveChangesAsync();
        return dbName;
    }

    private static void SetTenant(TenantAccessor accessor, Guid tenantId) =>
        accessor.SetTenant(new TenantContext { TenantId = tenantId, Slug = "t", Name = "T" });

    [TestMethod]
    public async Task NoTenant_SeesNoTenantScopedRows_OnlyPlatformRowsOfOptionalEntities()
    {
        var (db, _) = CreateDb(await SeedAsync());
        await using var _db = db;

        Assert.AreEqual(0, await db.Clients.CountAsync(), "required-tenant entities must be invisible without a tenant");
        Assert.AreEqual(0, await db.Users.CountAsync());
        Assert.AreEqual(0, await db.ClientScopes.CountAsync(), "child entities follow their parent's tenant");
        CollectionAssert.AreEqual(new[] { "openid" }, await db.Scopes.Select(s => s.Name).ToArrayAsync(),
            "optional-tenant entities expose only their platform-wide (TenantId == null) rows");
    }

    [TestMethod]
    public async Task WithTenant_SeesOnlyThatTenant()
    {
        var (db, accessor) = CreateDb(await SeedAsync());
        await using var _db = db;
        SetTenant(accessor, TenantA);

        CollectionAssert.AreEqual(new[] { "a" }, await db.Clients.Select(c => c.ClientId).ToArrayAsync());
        CollectionAssert.AreEquivalent(new[] { "openid", "a.scope" }, await db.Scopes.Select(s => s.Name).ToArrayAsync());
        Assert.AreEqual(1, await db.ClientScopes.CountAsync());
    }

    [TestMethod]
    public async Task SystemScope_IsExplicit_AndEndsWhenDisposed()
    {
        var (db, _) = CreateDb(await SeedAsync());
        await using var _db = db;

        using (TenantFilterScope.BeginSystemScope())
        {
            Assert.IsTrue(TenantFilterScope.IsSystemScope);
            Assert.AreEqual(2, await db.Clients.CountAsync());
            Assert.AreEqual(2, await db.ClientScopes.CountAsync());
            Assert.AreEqual(3, await db.Scopes.CountAsync());
        }

        Assert.IsFalse(TenantFilterScope.IsSystemScope);
        Assert.AreEqual(0, await db.Clients.CountAsync());
    }

    [TestMethod]
    public async Task SystemScope_OpenedInsideAnAsyncMethod_DoesNotLeakToTheCaller()
    {
        var (db, _) = CreateDb(await SeedAsync());
        await using var _db = db;

        async Task<int> CountInsideScopeAsync()
        {
            using var scope = TenantFilterScope.BeginSystemScope();
            return await db.Clients.CountAsync();
        }

        Assert.AreEqual(2, await CountInsideScopeAsync());
        Assert.IsFalse(TenantFilterScope.IsSystemScope);
        Assert.AreEqual(0, await db.Clients.CountAsync());
    }

    [TestMethod]
    public async Task BeginSystemScopeWhenTenantless_OnlyBypassesWithoutTenant()
    {
        var (db, accessor) = CreateDb(await SeedAsync());
        await using var _db = db;

        using (db.BeginSystemScopeWhenTenantless())
        {
            Assert.AreEqual(2, await db.Clients.CountAsync());
        }

        SetTenant(accessor, TenantB);
        using (var scope = db.BeginSystemScopeWhenTenantless())
        {
            Assert.IsNull(scope);
            CollectionAssert.AreEqual(new[] { "b" }, await db.Clients.Select(c => c.ClientId).ToArrayAsync());
        }
    }

    [TestMethod]
    public async Task FailOpenEscapeHatch_RestoresLegacyBehaviour_OnlyWithoutTenant()
    {
        var (db, accessor) = CreateDb(await SeedAsync(), failOpen: true);
        await using var _db = db;

        Assert.AreEqual(2, await db.Clients.CountAsync(), "MultiTenancy:TenantFilterFailOpen=true: no tenant => every tenant");
        SetTenant(accessor, TenantA);
        Assert.AreEqual(1, await db.Clients.CountAsync(), "a set tenant is still enforced");
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("false", false)]
    [DataRow("true", true)]
    public void FailOpenSetting_IsReadFromConfiguration_DefaultFalse(string? value, bool expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TenantFilterOptions.ConfigurationKey] = value })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMrWhoOidcAuthCore(config);
        using var provider = services.BuildServiceProvider();

        Assert.AreEqual(expected, provider.GetRequiredService<TenantFilterOptions>().FailOpen);
    }

    [TestMethod]
    [DataRow("/platform-admin", true, true)]
    [DataRow("/platform-admin/api/tenants", true, true)]
    [DataRow("/health/backchannel", true, true)]
    [DataRow("/platform-admin", false, false)]
    [DataRow("/t/acme/admin/clients", true, false)]
    [DataRow("/account/profile", true, false)]
    public async Task PlatformSystemScopeMiddleware_OpensScopeOnlyOnTenantlessPlatformPaths(string path, bool tenantless, bool expected)
    {
        var accessor = new TenantAccessor();
        if (!tenantless)
        {
            SetTenant(accessor, TenantA);
        }

        bool? observed = null;
        var middleware = new PlatformSystemScopeMiddleware(_ =>
        {
            observed = TenantFilterScope.IsSystemScope;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context, accessor);

        Assert.AreEqual(expected, observed);
        Assert.IsFalse(TenantFilterScope.IsSystemScope, "the scope must end with the request");
    }
}
