using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.MultiTenancy;

/// <summary>
/// End-to-end integration tests for multi-tenant functionality.
/// Tests the full workflow from tenant resolution through client operations and data isolation.
/// </summary>
[TestClass]
public class MultiTenantE2ETests
{
    private ServiceProvider _serviceProvider = null!;
    private AuthDbContext _db = null!;
    private Guid _tenant1Id;
    private Guid _tenant2Id;
    private Guid _realm1Id;
    private Guid _realm2Id;

    [TestInitialize]
    public async Task Setup()
    {
        var services = new ServiceCollection();

        // In-memory database for testing - register as singleton so all services share the same instance
        services.AddDbContext<AuthDbContext>(options =>
            options.UseInMemoryDatabase(databaseName: $"MultiTenantE2E_{Guid.NewGuid()}"),
            ServiceLifetime.Singleton);

        // Memory cache
        services.AddMemoryCache();

        // Multi-tenancy options (multi-tenant mode enabled)
        var multiTenancyOptions = new MultiTenancyOptions
        {
            Enabled = true,
            DefaultTenantSlug = "default"
        };
        services.AddSingleton<IMultiTenancyOptions>(multiTenancyOptions);

        // Tenant resolver
        services.AddSingleton<ITenantResolver, ModeAwareTenantResolver>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<AuthDbContext>();

        // Seed test data
        _tenant1Id = Guid.NewGuid();
        _tenant2Id = Guid.NewGuid();
        _realm1Id = Guid.NewGuid();
        _realm2Id = Guid.NewGuid();

        // Tenant 1
        _db.Tenants.Add(new Tenant
        {
            Id = _tenant1Id,
            Slug = "acme",
            Name = "Acme Corporation",
            IssuerUri = "https://localhost:5001/t/acme",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Tenant 2
        _db.Tenants.Add(new Tenant
        {
            Id = _tenant2Id,
            Slug = "contoso",
            Name = "Contoso Ltd",
            IssuerUri = "https://localhost:5001/t/contoso",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Realms
        var realm1 = new Realm { Id = _realm1Id, Name = "acme-realm", DisplayName = "Acme Realm" };
        var realm2 = new Realm { Id = _realm2Id, Name = "contoso-realm", DisplayName = "Contoso Realm" };
        _db.Realms.AddRange(realm1, realm2);

        // Clients for each tenant
        var client1 = new ClientEntity
        {
            ClientId = "acme-client",
            ClientName = "Acme SPA",
            RealmId = _realm1Id,
            TenantId = _tenant1Id,
            RequirePkce = true,
            RequireConsent = false
        };

        var client2 = new ClientEntity
        {
            ClientId = "contoso-client",
            ClientName = "Contoso SPA",
            RealmId = _realm2Id,
            TenantId = _tenant2Id,
            RequirePkce = true,
            RequireConsent = false
        };

        _db.Clients.AddRange(client1, client2);

        // Users for each tenant
        var user1 = new User
        {
            Username = "alice@acme.com",
            Email = "alice@acme.com",
            Name = "Alice",
            TenantId = _tenant1Id
        };

        var user2 = new User
        {
            Username = "bob@contoso.com",
            Email = "bob@contoso.com",
            Name = "Bob",
            TenantId = _tenant2Id
        };

        _db.Users.AddRange(user1, user2);

        await _db.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        _serviceProvider.Dispose();
    }

    private sealed class DummyHasher : IPasswordHasher
    {
        public string Hash(string password) => password;
        public bool Verify(string password, string hash) => hash == password;
    }

    [TestMethod]
    public async Task ClientStore_WithTenantContext_ReturnsOnlyTenantClients()
    {
        // Arrange
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(_tenant1Id, "acme", "Acme Corporation");
        var clientStore = new ClientStore(_db, new DummyHasher(), tenantAccessor);

        // Act - find client from tenant 1
        var client1 = await clientStore.FindByClientIdAsync("acme-client");

        // Assert - should find tenant 1 client
        Assert.IsNotNull(client1, "Should find client from tenant 1");
        Assert.AreEqual("acme-client", client1.ClientId);
        Assert.AreEqual(_tenant1Id, client1.TenantId);

        // Act - try to find client from tenant 2
        var client2 = await clientStore.FindByClientIdAsync("contoso-client");

        // Assert - should NOT find client from different tenant
        Assert.IsNull(client2, "Should NOT find client from different tenant");
    }

    [TestMethod]
    public async Task ClientStore_WithDifferentTenantContext_ReturnsCorrectClient()
    {
        // Arrange
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(_tenant2Id, "contoso", "Contoso Ltd");
        var clientStore = new ClientStore(_db, new DummyHasher(), tenantAccessor);

        // Act - find client from tenant 2
        var client = await clientStore.FindByClientIdAsync("contoso-client");

        // Assert - should find tenant 2 client
        Assert.IsNotNull(client, "Should find client from tenant 2");
        Assert.AreEqual("contoso-client", client.ClientId);
        Assert.AreEqual(_tenant2Id, client.TenantId);
    }

    [TestMethod]
    public async Task Users_AreScopedToTenant()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        // Act - get users for tenant 1
        var tenant1Users = await db.Users
            .Where(u => u.TenantId == _tenant1Id)
            .ToListAsync();

        // Assert
        Assert.HasCount(1, tenant1Users, "Tenant 1 should have exactly 1 user");
        Assert.AreEqual("alice@acme.com", tenant1Users[0].Username);

        // Act - get users for tenant 2
        var tenant2Users = await db.Users
            .Where(u => u.TenantId == _tenant2Id)
            .ToListAsync();

        // Assert
        Assert.HasCount(1, tenant2Users, "Tenant 2 should have exactly 1 user");
        Assert.AreEqual("bob@contoso.com", tenant2Users[0].Username);
    }

    [TestMethod]
    public async Task TenantResolver_ResolvesCorrectTenantFromPath()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - resolve tenant 1
        var result1 = await resolver.ResolveTenantAsync("/t/acme/authorize");

        // Assert
        Assert.IsNotNull(result1, "Should successfully resolve tenant 1");
        Assert.AreEqual(_tenant1Id, result1.TenantId);
        Assert.AreEqual("acme", result1.Slug);

        // Act - resolve tenant 2
        var result2 = await resolver.ResolveTenantAsync("/t/contoso/authorize");

        // Assert
        Assert.IsNotNull(result2, "Should successfully resolve tenant 2");
        Assert.AreEqual(_tenant2Id, result2.TenantId);
        Assert.AreEqual("contoso", result2.Slug);
    }

    [TestMethod]
    public async Task Clients_WithSameClientId_InDifferentTenants_AreIsolated()
    {
        // Arrange - add clients with same client_id in different tenants
        var sameClientId = "shared-spa";

        var clientInTenant1 = new ClientEntity
        {
            ClientId = sameClientId,
            ClientName = "Tenant 1 SPA",
            RealmId = _realm1Id,
            TenantId = _tenant1Id,
            RequirePkce = true
        };

        var clientInTenant2 = new ClientEntity
        {
            ClientId = sameClientId,
            ClientName = "Tenant 2 SPA",
            RealmId = _realm2Id,
            TenantId = _tenant2Id,
            RequirePkce = false // Different setting
        };

        _db.Clients.AddRange(clientInTenant1, clientInTenant2);
        await _db.SaveChangesAsync();

        // Act - retrieve client from tenant 1 context
        var accessor1 = MockTenantAccessor.CreateWithTenant(_tenant1Id, "acme");
        var store1 = new ClientStore(_db, new DummyHasher(), accessor1);
        var result1 = await store1.FindByClientIdAsync(sameClientId);

        // Assert - should get tenant 1's version
        Assert.IsNotNull(result1);
        Assert.AreEqual("Tenant 1 SPA", result1.ClientName);
        Assert.IsTrue(result1.RequirePkce, "Should have tenant 1 settings");

        // Act - retrieve client from tenant 2 context
        var accessor2 = MockTenantAccessor.CreateWithTenant(_tenant2Id, "contoso");
        var store2 = new ClientStore(_db, new DummyHasher(), accessor2);
        var result2 = await store2.FindByClientIdAsync(sameClientId);

        // Assert - should get tenant 2's version
        Assert.IsNotNull(result2);
        Assert.AreEqual("Tenant 2 SPA", result2.ClientName);
        Assert.IsFalse(result2.RequirePkce, "Should have tenant 2 settings");
    }

    [TestMethod]
    public async Task TenantResolver_WithInactiveTenant_ReturnsNull()
    {
        // Arrange - suspend tenant 1
        var tenant = await _db.Tenants.FindAsync(_tenant1Id);
        Assert.IsNotNull(tenant);
        tenant.Status = TenantStatus.Suspended;
        await _db.SaveChangesAsync();

        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act
        var result = await resolver.ResolveTenantAsync("/t/acme/authorize");

        // Assert
        Assert.IsNull(result, "Should return null for suspended tenant");
    }

    [TestMethod]
    public async Task TenantResolver_WithNonExistentSlug_ReturnsNull()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act
        var result = await resolver.ResolveTenantAsync("/t/nonexistent/authorize");

        // Assert
        Assert.IsNull(result, "Should return null for non-existent tenant");
    }
}
