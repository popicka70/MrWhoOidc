using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth;
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

    private static async Task<List<string>> SeedAndListClientIdsAsync(string environmentName)
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
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
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

        return await db.Clients.IgnoreQueryFilters().Select(c => c.ClientId).ToListAsync();
    }
}
