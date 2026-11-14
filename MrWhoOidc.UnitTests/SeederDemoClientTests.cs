using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class SeederDemoClientTests
{
    private static readonly string[] DemoClientIds = ["blazor-web", "react-demo", "m2m-test-client", "test-api"];

    [TestMethod]
    public async Task SeedAsync_InProduction_DoesNotSeedDemoClients()
    {
        var clientIds = await SeedAndListClientIdsAsync(Environments.Production);

        foreach (var demo in DemoClientIds)
        {
            CollectionAssert.DoesNotContain(clientIds, demo, $"Production bootstrap must not seed demo client '{demo}' (localhost redirects).");
        }
        CollectionAssert.Contains(clientIds, "mrwho-admin", "The admin client is essential and is still seeded.");
    }

    [TestMethod]
    public async Task SeedAsync_InDevelopment_SeedsDemoClients()
    {
        var clientIds = await SeedAndListClientIdsAsync(Environments.Development);

        foreach (var demo in DemoClientIds)
        {
            CollectionAssert.Contains(clientIds, demo);
        }
    }

    [TestMethod]
    public async Task SeedAsync_InDevelopment_CreatesAnActiveSecretForBlazorWeb()
    {
        using var seeded = await SeedAsync(Environments.Development);

        var client = await seeded.Db.Clients
            .IgnoreQueryFilters()
            .SingleAsync(c => c.ClientId == "blazor-web");
        var active = await seeded.Db.ClientSecrets
            .Where(s => s.ClientId == client.Id
                     && s.ActivatedAtUtc != null
                     && s.RevokedAtUtc == null
                     && s.ExpiresAtUtc == null)
            .ToListAsync();

        Assert.AreEqual(1, active.Count, "blazor-web must have exactly one active ClientSecrets row.");
        Assert.IsTrue(active[0].IsPrimary);
    }

    [TestMethod]
    public async Task SeedAsync_WhenBlazorWebHasOnlyTheLegacyHash_BackfillsAnActiveSecret()
    {
        using var seeded = await SeedAsync(Environments.Development);

        var client = await seeded.Db.Clients
            .IgnoreQueryFilters()
            .SingleAsync(c => c.ClientId == "blazor-web");

#pragma warning disable CS0618 // the legacy hash is what this test exercises
        var legacyHash = client.ClientSecretHash;
#pragma warning restore CS0618
        Assert.IsFalse(string.IsNullOrEmpty(legacyHash));

        // Simulate a database created before the ClientSecrets table existed: only the legacy
        // ClientSecretHash remains.
        seeded.Db.ClientSecrets.RemoveRange(
            seeded.Db.ClientSecrets.Where(s => s.ClientId == client.Id));
        await seeded.Db.SaveChangesAsync();

        await seeded.Seeder.SeedAsync();

        var active = await seeded.Db.ClientSecrets
            .Where(s => s.ClientId == client.Id
                     && s.ActivatedAtUtc != null
                     && s.RevokedAtUtc == null)
            .ToListAsync();

        Assert.AreEqual(1, active.Count);
        Assert.AreEqual(legacyHash, active[0].SecretHash, "The operator-known credential must be preserved.");
        Assert.IsTrue(active[0].IsPrimary);
    }

    [TestMethod]
    public async Task RepairSeededClientSecretsAsync_WhenTheOnlySecretExpired_CreatesAnActiveSecret()
    {
        using var seeded = await SeedAsync(Environments.Development);

        var client = await seeded.Db.Clients
            .IgnoreQueryFilters()
            .SingleAsync(c => c.ClientId == "blazor-web");

#pragma warning disable CS0618 // the legacy hash is what this test exercises
        var legacyHash = client.ClientSecretHash;
#pragma warning restore CS0618
        Assert.IsFalse(string.IsNullOrEmpty(legacyHash));

        var existing = await seeded.Db.ClientSecrets
            .Where(s => s.ClientId == client.Id)
            .ToListAsync();
        foreach (var secret in existing)
        {
            secret.ExpiresAtUtc = DateTime.UtcNow.AddDays(-1);
        }
        await seeded.Db.SaveChangesAsync();

        // This is the production startup path: no SeedAsync, just the repair pass.
        await seeded.Seeder.RepairSeededClientSecretsAsync();

        var active = await seeded.Db.ClientSecrets
            .Where(s => s.ClientId == client.Id
                     && s.ActivatedAtUtc != null
                     && s.RevokedAtUtc == null
                     && (s.ExpiresAtUtc == null || s.ExpiresAtUtc > DateTime.UtcNow))
            .ToListAsync();

        Assert.AreEqual(1, active.Count, "The repair must restore exactly one active secret.");
        Assert.AreEqual(legacyHash, active[0].SecretHash, "The operator-known credential must be preserved.");
        Assert.IsTrue(active[0].IsPrimary);
        Assert.IsNull(active[0].ExpiresAtUtc, "The repaired secret must not inherit the expired row's expiry.");
    }

    private static async Task<List<string>> SeedAndListClientIdsAsync(string environmentName)
    {
        using var seeded = await SeedAsync(environmentName);
        return await seeded.Db.Clients.IgnoreQueryFilters().Select(c => c.ClientId).ToListAsync();
    }

    private static async Task<SeededScope> SeedAsync(string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:UserAccount:UserAccountDecouplingEnabled"] = "true"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddMrWhoOidcAuthCore(configuration);
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        // Mirror the bootstrap path: the tenant is set on the scope's accessor, which the DbContext's tenant filter reads (D17).
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
        tenantAccessor.SetTenant(MockTenantAccessor.CreateWithDefaultTenant().CurrentTenant!);
        db.Tenants.Add(new Tenant
        {
            Id = tenantAccessor.CurrentTenant!.TenantId,
            Slug = tenantAccessor.CurrentTenant.Slug,
            Name = tenantAccessor.CurrentTenant.Name,
            IssuerUri = tenantAccessor.CurrentTenant.IssuerUri,
            Status = TenantStatus.Active
        });
        await db.SaveChangesAsync();

        var env = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environmentName);
        var seeder = new Seeder(
            db,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            tenantAccessor,
            scope.ServiceProvider.GetRequiredService<IUserAccountProvisioner>(),
            env,
            NullLogger<Seeder>.Instance);

        await seeder.SeedAsync();

        return new SeededScope(provider, scope, db, seeder);
    }

    private sealed record SeededScope(
        ServiceProvider Provider,
        IServiceScope Scope,
        AuthDbContext Db,
        Seeder Seeder) : IDisposable
    {
        public void Dispose()
        {
            Scope.Dispose();
            Provider.Dispose();
        }
    }
}
