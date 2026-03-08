using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Services;

[TestClass]
public class ConfigurationImportServicePerformanceTests
{
    private AuthDbContext _db = null!;
    private Mock<IPasswordHasher> _passwordHasher = null!;
    private Mock<ILogger<ConfigurationImportService>> _logger = null!;
    private ConfigurationImportService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new AuthDbContext(options);

        _passwordHasher = new Mock<IPasswordHasher>();
        _logger = new Mock<ILogger<ConfigurationImportService>>();

        _service = new ConfigurationImportService(_db, _passwordHasher.Object, _logger.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [TestMethod]
    public async Task DetectConflictsAsync_PerformanceTest()
    {
        // 1. Arrange: Setup large dataset
        var tenantCount = 50;
        var realmsPerTenant = 5;
        var clientsPerTenant = 10;
        var providersPerTenant = 5;

        // Populate database
        for (int i = 0; i < tenantCount; i++)
        {
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = $"Existing Tenant {i}",
                Slug = $"tenant-{i}",
                CreatedAt = DateTime.UtcNow
            };
            _db.Tenants.Add(tenant);

            for (int r = 0; r < realmsPerTenant; r++)
            {
                _db.Realms.Add(new Realm
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.Id,
                    Name = $"realm-{r}",
                    DisplayName = $"Realm {r}",
                    CreatedAt = DateTime.UtcNow
                });
            }

            for (int c = 0; c < clientsPerTenant; c++)
            {
                _db.Clients.Add(new ClientEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.Id,
                    ClientId = $"client-{i}-{c}",
                    ClientName = $"Client {i} {c}"
                });
            }

            for (int p = 0; p < providersPerTenant; p++)
            {
                _db.IdentityProviders.Add(new IdentityProvider
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.Id,
                    Name = $"provider-{p}",
                    DisplayName = $"Provider {p}",
                    Type = IdentityProviderType.Oidc,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }
        await _db.SaveChangesAsync();

        // Setup ExportManifest
        var tenantsDef = new List<TenantSeedDefinition>();
        for (int i = 0; i < tenantCount; i++)
        {
            var tenantDef = new TenantSeedDefinition
            {
                Slug = $"tenant-{i}",
                Name = $"Tenant {i}",
                Realms = new List<RealmSeedDefinition>(),
                Clients = new List<ClientSeedDefinition>(),
                IdentityProviders = new List<IdentityProviderSeedDefinition>()
            };

            for (int r = 0; r < realmsPerTenant; r++)
            {
                tenantDef.Realms.Add(new RealmSeedDefinition { Name = $"realm-{r}", DisplayName = $"Realm {r}" });
            }

            for (int c = 0; c < clientsPerTenant; c++)
            {
                tenantDef.Clients.Add(new ClientSeedDefinition { ClientId = $"client-{i}-{c}", ClientName = $"Client {i} {c}" });
            }

            for (int p = 0; p < providersPerTenant; p++)
            {
                tenantDef.IdentityProviders.Add(new IdentityProviderSeedDefinition { Name = $"provider-{p}", DisplayName = $"Provider {p}" });
            }

            tenantsDef.Add(tenantDef);
        }

        var manifest = new ExportManifest
        {
            Version = 1,
            ExportType = "full",
            Data = new SeedManifest
            {
                Tenants = tenantsDef
            }
        };

        var importOptions = new ImportOptions();

        // 2. Act (warmup)
        // Reflection to call private method
        var methodInfo = typeof(ConfigurationImportService).GetMethod("DetectConflictsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task<List<ImportConflict>>)methodInfo!.Invoke(_service, new object[] { manifest, importOptions, CancellationToken.None })!;

        // Reset db context state just in case
        _db.ChangeTracker.Clear();

        // 3. Act (measured)
        var sw = Stopwatch.StartNew();
        var conflicts = await (Task<List<ImportConflict>>)methodInfo.Invoke(_service, new object[] { manifest, importOptions, CancellationToken.None })!;
        sw.Stop();

        // 4. Assert
        Console.WriteLine($"DetectConflictsAsync execution time: {sw.ElapsedMilliseconds} ms");
        Assert.AreEqual(tenantCount + (tenantCount * realmsPerTenant) + (tenantCount * clientsPerTenant) + (tenantCount * providersPerTenant), conflicts.Count);
    }
}
