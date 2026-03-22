using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.MultiTenancy;

/// <summary>
/// Service audit tests to verify all core services properly filter by TenantId.
/// These tests ensure critical authentication and authorization services respect tenant boundaries.
/// </summary>
[TestClass]
public class ServiceAuditTests
{
    private ServiceProvider _serviceProvider = null!;
    private AuthDbContext _db = null!;
    private Guid _tenant1Id;
    private Guid _tenant2Id;
    private MockTenantAccessor _tenantAccessor = null!;

    [TestInitialize]
    public async Task Setup()
    {
        var services = new ServiceCollection();

        // Configure in-memory database
        services.AddDbContext<AuthDbContext>(options =>
            options.UseInMemoryDatabase($"ServiceAuditTests_{Guid.NewGuid()}"));

        // Add required services
        services.AddSingleton<IPasswordHasher, DummyHasher>();
        services.AddSingleton<ITenantSettingsService, MockTenantSettingsService>();
        services.AddSingleton<HybridCache, TestHybridCache>();

        // Mock tenant accessor
        _tenantAccessor = new MockTenantAccessor();
        services.AddSingleton<ITenantAccessor>(_tenantAccessor);

        // Add services under test
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<AuthDbContext>();

        // Seed test data
        await SeedTestDataAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        _serviceProvider?.Dispose();
    }

    private async Task SeedTestDataAsync()
    {
        // Create two tenants
        var tenant1 = new Tenant { Id = Guid.NewGuid(), Name = "Acme Corp", Slug = "acme", Status = TenantStatus.Active };
        var tenant2 = new Tenant { Id = Guid.NewGuid(), Name = "Contoso Ltd", Slug = "contoso", Status = TenantStatus.Active };

        _tenant1Id = tenant1.Id;
        _tenant2Id = tenant2.Id;

        _db.Tenants.AddRange(tenant1, tenant2);
        await _db.SaveChangesAsync();
    }

    #region UserService Tests

    [TestMethod]
    public async Task UserService_FindByUsernameAsync_FiltersByTenantId()
    {
        // Arrange: Create user "alice" in Tenant 1
        var user1 = new User
        {
            Username = "alice",
            Email = "alice@acme.com",
            TenantId = _tenant1Id
        };
        _db.Users.Add(user1);
        await _db.SaveChangesAsync();

        var userService = _serviceProvider.GetRequiredService<IUserService>();

        // Act & Assert: Set context to Tenant 1 - should find user
        _tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenant1Id,
            Slug = "acme",
            Name = "Acme Corp",
            IsMultiTenantMode = true
        });
        var foundInTenant1 = await userService.FindByUsernameAsync("alice");
        Assert.IsNotNull(foundInTenant1, "User should be found in Tenant 1");
        Assert.AreEqual(user1.Id, foundInTenant1.Id);

        // Act & Assert: Switch to Tenant 2 - should NOT find user
        _tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenant2Id,
            Slug = "contoso",
            Name = "Contoso Ltd",
            IsMultiTenantMode = true
        });
        var foundInTenant2 = await userService.FindByUsernameAsync("alice");
        Assert.IsNull(foundInTenant2, "User from Tenant 1 should NOT be visible in Tenant 2");
    }

    [TestMethod]
    public async Task UserService_SameUsername_DifferentTenants_IsolatedCorrectly()
    {
        // Arrange: Create user "bob" with different passwords in both tenants
        var user1 = new User
        {
            Username = "bob",
            Email = "bob@acme.com",
            TenantId = _tenant1Id
        };
        var user2 = new User
        {
            Username = "bob",
            Email = "bob@contoso.com",
            TenantId = _tenant2Id
        };
        _db.Users.AddRange(user1, user2);
        await _db.SaveChangesAsync();

        var userService = _serviceProvider.GetRequiredService<IUserService>();

        // Act & Assert: Tenant 1 context
        _tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenant1Id,
            Slug = "acme",
            Name = "Acme Corp",
            IsMultiTenantMode = true
        });
        var bobInTenant1 = await userService.FindByUsernameAsync("bob");
        Assert.IsNotNull(bobInTenant1);
        Assert.AreEqual(_tenant1Id, bobInTenant1.TenantId);

        // Act & Assert: Tenant 2 context
        _tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenant2Id,
            Slug = "contoso",
            Name = "Contoso Ltd",
            IsMultiTenantMode = true
        });
        var bobInTenant2 = await userService.FindByUsernameAsync("bob");
        Assert.IsNotNull(bobInTenant2);
        Assert.AreEqual(_tenant2Id, bobInTenant2.TenantId);

        // Verify they are different users
        Assert.AreNotEqual(bobInTenant1.Id, bobInTenant2.Id, "Users with same username in different tenants should have different IDs");
    }

    [TestMethod]
    public async Task UserService_FindByUsernameOrEmailAsync_FiltersByTenantId()
    {
        // Arrange: Create users with same email pattern in different tenants
        var user1 = new User
        {
            Username = "admin",
            Email = "admin@example.com",
            NormalizedEmail = "ADMIN@EXAMPLE.COM",
            TenantId = _tenant1Id
        };
        var user2 = new User
        {
            Username = "admin",
            Email = "admin@example.com",
            NormalizedEmail = "ADMIN@EXAMPLE.COM",
            TenantId = _tenant2Id
        };
        _db.Users.AddRange(user1, user2);
        await _db.SaveChangesAsync();

        var userService = _serviceProvider.GetRequiredService<IUserService>();

        // Act & Assert: Search by email in Tenant 1
        _tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenant1Id,
            Slug = "acme",
            Name = "Acme Corp",
            IsMultiTenantMode = true
        });
        var found1 = await userService.FindByUsernameOrEmailAsync("admin@example.com");
        Assert.IsNotNull(found1);
        Assert.AreEqual(_tenant1Id, found1.TenantId);

        // Act & Assert: Search by email in Tenant 2
        _tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenant2Id,
            Slug = "contoso",
            Name = "Contoso Ltd",
            IsMultiTenantMode = true
        });
        var found2 = await userService.FindByUsernameOrEmailAsync("admin@example.com");
        Assert.IsNotNull(found2);
        Assert.AreEqual(_tenant2Id, found2.TenantId);

        // Verify isolation
        Assert.AreNotEqual(found1.Id, found2.Id, "Different tenant contexts should return different users");
    }

    #endregion

    #region RefreshTokenService Tests

    [TestMethod]
    public async Task RefreshTokenService_CreateRefreshToken_SetsTenantIdCorrectly()
    {
        // Arrange: Create user in Tenant 1
        var user1 = new User
        {
            Username = "charlie",
            Email = "charlie@acme.com",
            TenantId = _tenant1Id
        };
        _db.Users.Add(user1);
        await _db.SaveChangesAsync();

        var refreshTokenService = _serviceProvider.GetRequiredService<IRefreshTokenService>();

        // Act: Create refresh token in Tenant 1 context
        _tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenant1Id,
            Slug = "acme",
            Name = "Acme Corp",
            IsMultiTenantMode = true
        });
        var (token, hash) = await refreshTokenService.CreateRefreshTokenAsync(
            user1.Id,
            "client1",
            ["openid", "profile"]);

        // Assert: Verify token is stored with correct TenantId
        var storedToken = await _db.Tokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        Assert.IsNotNull(storedToken, "Token should be stored in database");
        Assert.AreEqual(_tenant1Id, storedToken.TenantId, "Token should have Tenant 1 ID");
        Assert.AreEqual(user1.Id, storedToken.UserId);
        Assert.AreEqual("refresh", storedToken.Type);
    }

    [TestMethod]
    public async Task RefreshTokenService_CrossTenantAccess_TokensIsolated()
    {
        // Arrange: Create users in both tenants
        var user1 = new User { Username = "diana", TenantId = _tenant1Id };
        var user2 = new User { Username = "evan", TenantId = _tenant2Id };
        _db.Users.AddRange(user1, user2);
        await _db.SaveChangesAsync();

        var refreshTokenService = _serviceProvider.GetRequiredService<IRefreshTokenService>();

        // Act: Create token in Tenant 1
        _tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenant1Id,
            Slug = "acme",
            Name = "Acme Corp",
            IsMultiTenantMode = true
        });
        var (token1, hash1) = await refreshTokenService.CreateRefreshTokenAsync(
            user1.Id, "client1", ["openid"]);

        // Act: Create token in Tenant 2
        _tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenant2Id,
            Slug = "contoso",
            Name = "Contoso Ltd",
            IsMultiTenantMode = true
        });
        var (token2, hash2) = await refreshTokenService.CreateRefreshTokenAsync(
            user2.Id, "client1", ["openid"]);

        // Assert: Verify tokens are isolated by tenant
        var tenant1Tokens = await _db.Tokens
            .Where(t => t.TenantId == _tenant1Id && t.Type == "refresh")
            .ToListAsync();
        var tenant2Tokens = await _db.Tokens
            .Where(t => t.TenantId == _tenant2Id && t.Type == "refresh")
            .ToListAsync();

        Assert.HasCount(1, tenant1Tokens, "Tenant 1 should have exactly 1 refresh token");
        Assert.HasCount(1, tenant2Tokens, "Tenant 2 should have exactly 1 refresh token");
        Assert.AreEqual(hash1, tenant1Tokens[0].TokenHash);
        Assert.AreEqual(hash2, tenant2Tokens[0].TokenHash);

        // Verify no cross-tenant visibility at database level
        var tenant1CanSeeTenant2Token = await _db.Tokens
            .AnyAsync(t => t.TenantId == _tenant1Id && t.TokenHash == hash2);
        Assert.IsFalse(tenant1CanSeeTenant2Token, "Tenant 1 should not see Tenant 2's tokens");
    }

    #endregion

    private sealed class DummyHasher : IPasswordHasher
    {
        public string Hash(string password) => password;
        public bool Verify(string password, string hash) => hash == password;
    }
}
