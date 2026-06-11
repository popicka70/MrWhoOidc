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
public sealed class PasswordHasherTests
{
    [TestMethod]
    public void Hash_ProducesEncodedV2HashThatRoundTrips()
    {
        using var provider = CreateProvider();
        var hasher = provider.GetRequiredService<IPasswordHasher>();

        var hash = hasher.Hash("Admin123!");

        Assert.IsTrue(hash.StartsWith("v2:$argon2", StringComparison.Ordinal));
        Assert.IsFalse(hash.Contains("SecureArray", StringComparison.Ordinal));
        Assert.IsTrue(hasher.Verify("Admin123!", hash));
        Assert.IsFalse(hasher.Verify("wrong-password", hash));
    }

    [TestMethod]
    public async Task SeedAsync_RepairsMalformedSeededAdminHash_FromEnvironmentPassword()
    {
        const string adminPassword = "Admin123!";
        var previousPassword = Environment.GetEnvironmentVariable("SEED_ADMIN_PASSWORD");
        Environment.SetEnvironmentVariable("SEED_ADMIN_PASSWORD", adminPassword);

        try
        {
            using var provider = CreateProvider();
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var provisioner = scope.ServiceProvider.GetRequiredService<IUserAccountProvisioner>();
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

            var env = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Development");
            var seeder = new Seeder(db, hasher, tenantAccessor, provisioner, env, NullLogger<Seeder>.Instance);

            await seeder.SeedAsync();

            var adminAccount = await db.UserAccounts.SingleAsync(a => a.Username == "admin");
            adminAccount.PasswordHash = "v2:Isopoh.Cryptography.SecureArray.SecureArray`1[System.Byte]";
            adminAccount.HashAlgorithm = "argon2id";
            await db.SaveChangesAsync();

            await seeder.SeedAsync();

            db.ChangeTracker.Clear();
            var repairedAdminAccount = await db.UserAccounts.SingleAsync(a => a.Username == "admin");

            Assert.IsTrue(repairedAdminAccount.PasswordHash.StartsWith("v2:$argon2", StringComparison.Ordinal));
            Assert.IsFalse(repairedAdminAccount.PasswordHash.Contains("SecureArray", StringComparison.Ordinal));
            Assert.IsTrue(hasher.Verify(adminPassword, repairedAdminAccount.PasswordHash));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEED_ADMIN_PASSWORD", previousPassword);
        }
    }

    private static ServiceProvider CreateProvider()
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
        return services.BuildServiceProvider();
    }
}