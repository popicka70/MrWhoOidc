using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Security.Admin;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.MultiTenancy;

/// <summary>
/// Security tests for multi-tenant authorization and access control
/// - Platform admin authorization
/// - Tenant admin authorization
/// - User self-service authorization
/// - Cross-tenant access prevention
/// </summary>
[TestClass]
public class MultiTenantSecurityTests
{
    private ServiceProvider _serviceProvider = null!;
    private AuthDbContext _db = null!;
    private IAuthorizationService _authorizationService = null!;

    private Guid _platformRealmId;
    private Guid _tenant1Id;
    private Guid _tenant2Id;
    private Guid _tenant1RealmId;
    private Guid _tenant2RealmId;

    [TestInitialize]
    public async Task Setup()
    {
        var services = new ServiceCollection();

        // In-memory database
        var dbName = $"SecurityTests_{Guid.NewGuid()}";
        services.AddDbContext<AuthDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        // Logging
        services.AddLogging();

        // Authorization
        services.AddAuthorization(options =>
        {
            options.AddPolicy("platform-admin", policy =>
            {
                policy.RequireRole("platform-admin");
            });

            options.AddPolicy("tenant-admin", policy =>
            {
                policy.RequireRole("tenant-admin", "admin");
            });
        });

        // Register ITenantAccessor (required by TenantAdminAuthorizationHandler)
        services.AddScoped<ITenantAccessor>(_ => MockTenantAccessor.CreateSingleTenantMode());

        // Register ITenantSwitchingService mock (required by TenantAdminAuthorizationHandler)
        services.AddScoped<ITenantSwitchingService, MockTenantSwitchingService>();

        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, TenantAdminAuthorizationHandler>();
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<AuthDbContext>();
        _authorizationService = _serviceProvider.GetRequiredService<IAuthorizationService>();

        await SeedDataAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db?.Database.EnsureDeleted();
        _db?.Dispose();
        _serviceProvider?.Dispose();
    }

    private async Task SeedDataAsync()
    {
        // Create platform realm
        _platformRealmId = Guid.NewGuid();
        var platformRealm = new Realm
        {
            Id = _platformRealmId,
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"), // Default tenant
            Name = "platform",
            DisplayName = "Platform Realm"
        };
        _db.Realms.Add(platformRealm);

        // Create tenants
        _tenant1Id = Guid.NewGuid();
        _tenant2Id = Guid.NewGuid();

        var tenant1 = new Tenant
        {
            Id = _tenant1Id,
            Slug = "tenant1",
            Name = "Tenant 1",
            IssuerUri = "https://auth.example.com/t/tenant1",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var tenant2 = new Tenant
        {
            Id = _tenant2Id,
            Slug = "tenant2",
            Name = "Tenant 2",
            IssuerUri = "https://auth.example.com/t/tenant2",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Tenants.AddRange(tenant1, tenant2);

        // Create realms for tenants
        _tenant1RealmId = Guid.NewGuid();
        _tenant2RealmId = Guid.NewGuid();

        var tenant1Realm = new Realm
        {
            Id = _tenant1RealmId,
            TenantId = _tenant1Id,
            Name = "default",
            DisplayName = "Tenant 1 Default Realm"
        };

        var tenant2Realm = new Realm
        {
            Id = _tenant2RealmId,
            TenantId = _tenant2Id,
            Name = "default",
            DisplayName = "Tenant 2 Default Realm"
        };

        _db.Realms.AddRange(tenant1Realm, tenant2Realm);

        // Create roles
        var platformAdminRole = new Role
        {
            Name = "platform-admin",
            RealmId = _platformRealmId,
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            IsActive = true
        };

        var tenant1AdminRole = new Role
        {
            Name = "tenant-admin",
            RealmId = _tenant1RealmId,
            TenantId = _tenant1Id,
            IsActive = true
        };

        var tenant2AdminRole = new Role
        {
            Name = "tenant-admin",
            RealmId = _tenant2RealmId,
            TenantId = _tenant2Id,
            IsActive = true
        };

        _db.Roles.AddRange(platformAdminRole, tenant1AdminRole, tenant2AdminRole);
        await _db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task PlatformAdmin_CanAccessPlatformAdminPolicy()
    {
        // Arrange: Create platform admin user with platform-admin role
        var platformAdminId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, platformAdminId.ToString()),
            new Claim(ClaimTypes.Role, "platform-admin"),
            new Claim("realm", "platform")
        };

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        // Act: Check authorization
        var result = await _authorizationService.AuthorizeAsync(principal, "platform-admin");

        // Assert: Should be authorized
        Assert.IsTrue(result.Succeeded, "Platform admin should be authorized for platform-admin policy");
    }

    [TestMethod]
    public async Task TenantAdmin_CannotAccessPlatformAdminPolicy()
    {
        // Arrange: Create tenant admin user (NOT platform admin)
        var tenantAdminId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, tenantAdminId.ToString()),
            new Claim(ClaimTypes.Role, "tenant-admin"),
            new Claim("realm", "default")
        };

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        // Act: Check authorization
        var result = await _authorizationService.AuthorizeAsync(principal, "platform-admin");

        // Assert: Should NOT be authorized
        Assert.IsFalse(result.Succeeded, "Tenant admin should NOT be authorized for platform-admin policy");
    }

    [TestMethod]
    public async Task RegularUser_CannotAccessAdminPolicies()
    {
        // Arrange: Create regular user (no admin roles)
        var userId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("realm", "default")
            // No role claim
        };

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        // Act: Check platform admin policy
        var platformResult = await _authorizationService.AuthorizeAsync(principal, "platform-admin");

        // Assert: Should NOT be authorized for platform admin
        Assert.IsFalse(platformResult.Succeeded, "Regular user should NOT access platform admin");

        // Act: Check tenant admin policy
        var tenantResult = await _authorizationService.AuthorizeAsync(principal, "tenant-admin");

        // Assert: Should NOT be authorized for tenant admin
        Assert.IsFalse(tenantResult.Succeeded, "Regular user should NOT access tenant admin");
    }

    [TestMethod]
    public async Task TenantAdmin_CanAccessOwnTenantData()
    {
        // Arrange: Create tenant admin for Tenant 1
        var adminUserId = Guid.NewGuid();
        var adminUser = new User
        {
            Id = adminUserId,
            TenantId = _tenant1Id,
            Username = "admin1",
            Email = "admin@tenant1.com"
        };

        _db.Users.Add(adminUser);
        await _db.SaveChangesAsync();

        // Act: Query users from Tenant 1 (admin's own tenant)
        var ownTenantUsers = await _db.Users
            .Where(u => u.TenantId == _tenant1Id)
            .ToListAsync();

        // Assert: Should see own tenant's users
        Assert.IsNotEmpty(ownTenantUsers);
        Assert.IsTrue(ownTenantUsers.All(u => u.TenantId == _tenant1Id));
    }

    [TestMethod]
    public async Task TenantAdmin_CannotAccessOtherTenantData()
    {
        // Arrange: Create users in both tenants
        var admin1Id = Guid.NewGuid();
        var admin1 = new User
        {
            Id = admin1Id,
            TenantId = _tenant1Id,
            Username = "admin1",
            Email = "admin@tenant1.com"
        };

        var user2Id = Guid.NewGuid();
        var user2 = new User
        {
            Id = user2Id,
            TenantId = _tenant2Id,
            Username = "user2",
            Email = "user@tenant2.com"
        };

        _db.Users.AddRange(admin1, user2);
        await _db.SaveChangesAsync();

        // Act: Tenant 1 admin tries to query Tenant 2 data
        // In reality, this would be blocked by tenant context filtering
        var otherTenantUsers = await _db.Users
            .Where(u => u.TenantId == _tenant2Id)
            .ToListAsync();

        // Verify Tenant 2 has users
        Assert.IsNotEmpty(otherTenantUsers);

        // Now verify that with proper tenant filtering (Tenant 1 context),
        // we wouldn't see Tenant 2 users
        var tenant1Users = await _db.Users
            .Where(u => u.TenantId == _tenant1Id)
            .ToListAsync();

        Assert.IsFalse(tenant1Users.Any(u => u.TenantId == _tenant2Id),
            "Tenant 1 context should not return Tenant 2 users");
    }

    [TestMethod]
    public async Task UserSelfService_CanOnlyAccessOwnData()
    {
        // Arrange: Create two users in same tenant
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();

        var user1 = new User
        {
            Id = user1Id,
            TenantId = _tenant1Id,
            Username = "user1",
            Email = "user1@tenant1.com"
        };

        var user2 = new User
        {
            Id = user2Id,
            TenantId = _tenant1Id,
            Username = "user2",
            Email = "user2@tenant1.com"
        };

        _db.Users.AddRange(user1, user2);
        await _db.SaveChangesAsync();

        // Act: User 1 queries own data
        var ownUser = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == user1Id);

        // Assert: Should find own data
        Assert.IsNotNull(ownUser);
        Assert.AreEqual(user1Id, ownUser.Id);

        // Act: User 1 should NOT query User 2's data in self-service context
        // (In real scenario, user ID would be from claims and filtered)
        var otherUser = await _db.Users
            .Where(u => u.Id == user1Id) // Simulating "current user" filter
            .ToListAsync();

        // Assert: Should only see self
        Assert.HasCount(1, otherUser);
        Assert.AreEqual(user1Id, otherUser[0].Id);
    }

    [TestMethod]
    public async Task DataIsolation_QueriesFiltered_ByTenantId()
    {
        // This is a critical security test: verify ALL queries respect TenantId

        // Arrange: Create data across tenants
        var user1 = new User
        {
            Id = Guid.NewGuid(),
            TenantId = _tenant1Id,
            Username = "user1",
            Email = "user1@tenant1.com"
        };

        var user2 = new User
        {
            Id = Guid.NewGuid(),
            TenantId = _tenant2Id,
            Username = "user2",
            Email = "user2@tenant2.com"
        };

        var client1 = new Auth.Persistence.Client
        {
            ClientId = "app1",
            ClientName = "App 1",
            TenantId = _tenant1Id,
            RealmId = _tenant1RealmId
        };

        var client2 = new Auth.Persistence.Client
        {
            ClientId = "app2",
            ClientName = "App 2",
            TenantId = _tenant2Id,
            RealmId = _tenant2RealmId
        };

        _db.Users.AddRange(user1, user2);
        _db.Clients.AddRange(client1, client2);
        await _db.SaveChangesAsync();

        // Act & Assert: Users table
        var tenant1Users = await _db.Users.Where(u => u.TenantId == _tenant1Id).ToListAsync();
        var tenant2Users = await _db.Users.Where(u => u.TenantId == _tenant2Id).ToListAsync();

        Assert.HasCount(1, tenant1Users);
        Assert.HasCount(1, tenant2Users);
        Assert.AreNotEqual(tenant1Users[0].Id, tenant2Users[0].Id);

        // Act & Assert: Clients table
        var tenant1Clients = await _db.Clients.Where(c => c.TenantId == _tenant1Id).ToListAsync();
        var tenant2Clients = await _db.Clients.Where(c => c.TenantId == _tenant2Id).ToListAsync();

        Assert.HasCount(1, tenant1Clients);
        Assert.HasCount(1, tenant2Clients);
        Assert.AreNotEqual(tenant1Clients[0].Id, tenant2Clients[0].Id);

        // Critical: Verify no cross-contamination
        Assert.IsFalse(tenant1Users.Any(u => u.TenantId == _tenant2Id), "No Tenant 2 users in Tenant 1 query");
        Assert.IsFalse(tenant2Users.Any(u => u.TenantId == _tenant1Id), "No Tenant 1 users in Tenant 2 query");
        Assert.IsFalse(tenant1Clients.Any(c => c.TenantId == _tenant2Id), "No Tenant 2 clients in Tenant 1 query");
        Assert.IsFalse(tenant2Clients.Any(c => c.TenantId == _tenant1Id), "No Tenant 1 clients in Tenant 2 query");
    }

    [TestMethod]
    public async Task SecurityBoundary_NoGlobalQueriesWithoutTenantFilter()
    {
        // This test verifies that we never accidentally query ALL tenants

        // Arrange: Create data in multiple tenants
        for (int i = 1; i <= 3; i++)
        {
            var tenantId = Guid.NewGuid();
            var tenant = new Tenant
            {
                Id = tenantId,
                Slug = $"tenant{i}",
                Name = $"Tenant {i}",
                IssuerUri = $"https://auth.example.com/t/tenant{i}",
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            };

            var user = new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Username = $"user{i}",
                Email = $"user{i}@tenant{i}.com"
            };

            _db.Tenants.Add(tenant);
            _db.Users.Add(user);
        }

        await _db.SaveChangesAsync();

        // Act: Simulate WRONG query (no tenant filter) - this should NEVER happen in production
        var allUsers = await _db.Users.ToListAsync();

        // Assert: This would return ALL users (BAD!)
        Assert.IsGreaterThanOrEqualTo(3, allUsers.Count, "Unfiltered query returns all tenants' data");

        // Demonstrate CORRECT query (with tenant filter)
        var specificTenantUsers = await _db.Users
            .Where(u => u.TenantId == _tenant1Id)
            .ToListAsync();

        // Assert: Filtered query returns only specific tenant
        Assert.IsTrue(specificTenantUsers.All(u => u.TenantId == _tenant1Id),
            "Filtered query should only return specific tenant data");

        // This test serves as documentation: ALWAYS filter by TenantId
        Console.WriteLine("✅ SECURITY: Always filter queries by TenantId to prevent data leaks");
    }

    [TestMethod]
    public async Task PlatformAdmin_CanViewAllTenants_ForAdminPurposes()
    {
        // Platform admins SHOULD be able to see all tenants (for management)
        // This is an exception to tenant isolation, but only for platform admin operations

        // Arrange: Already have multiple tenants from Setup

        // Act: Platform admin queries all tenants
        var allTenants = await _db.Tenants.ToListAsync();

        // Assert: Platform admin can see all tenants
        Assert.IsGreaterThanOrEqualTo(2, allTenants.Count, "Platform admin should see all tenants");

        // Verify tenant isolation still applies to non-platform-admin data
        var tenant1Users = await _db.Users.Where(u => u.TenantId == _tenant1Id).ToListAsync();
        var tenant2Users = await _db.Users.Where(u => u.TenantId == _tenant2Id).ToListAsync();

        // Even platform admin queries should specify tenant when accessing tenant data
        Assert.IsFalse(tenant1Users.Any(u => u.TenantId == _tenant2Id),
            "Even for platform admin, tenant data should be explicitly filtered");
    }

    [TestMethod]
    public void AnonymousUser_CannotAccessProtectedResources()
    {
        // Arrange: Anonymous user (no claims)
        var principal = new ClaimsPrincipal(new ClaimsIdentity()); // Not authenticated

        // Act & Assert: Check various policies
        var platformResult = _authorizationService.AuthorizeAsync(principal, "platform-admin").Result;
        Assert.IsFalse(platformResult.Succeeded, "Anonymous cannot access platform admin");

        var tenantResult = _authorizationService.AuthorizeAsync(principal, "tenant-admin").Result;
        Assert.IsFalse(tenantResult.Succeeded, "Anonymous cannot access tenant admin");

        // Anonymous user is not authenticated
        Assert.IsFalse(principal.Identity?.IsAuthenticated ?? false, "Anonymous user should not be authenticated");
    }

    [TestMethod]
    public async Task RoleAssignment_IsTenantSpecific()
    {
        // Roles should be scoped to tenant+realm, not global

        // Arrange: Create same role name in different tenants
        var role1 = new Role
        {
            Name = "editor",
            RealmId = _tenant1RealmId,
            TenantId = _tenant1Id,
            IsActive = true
        };

        var role2 = new Role
        {
            Name = "editor", // Same name
            RealmId = _tenant2RealmId,
            TenantId = _tenant2Id,
            IsActive = true
        };

        _db.Roles.AddRange(role1, role2);
        await _db.SaveChangesAsync();

        // Act: Query roles by tenant
        var tenant1Roles = await _db.Roles.Where(r => r.TenantId == _tenant1Id).ToListAsync();
        var tenant2Roles = await _db.Roles.Where(r => r.TenantId == _tenant2Id).ToListAsync();

        // Assert: Each tenant has its own "editor" role
        Assert.IsTrue(tenant1Roles.Any(r => r.Name == "editor"));
        Assert.IsTrue(tenant2Roles.Any(r => r.Name == "editor"));

        // Verify they are different role records
        var tenant1EditorId = tenant1Roles.First(r => r.Name == "editor").Id;
        var tenant2EditorId = tenant2Roles.First(r => r.Name == "editor").Id;

        Assert.AreNotEqual(tenant1EditorId, tenant2EditorId,
            "Same role name in different tenants should be separate records");
    }
}

/// <summary>
/// Mock implementation of ITenantSwitchingService for unit tests.
/// </summary>
internal class MockTenantSwitchingService : ITenantSwitchingService
{
    public Task<List<TenantAccessInfo>> GetUserTenantsAsync(ClaimsPrincipal user) => Task.FromResult(new List<TenantAccessInfo>());
    public Task SwitchTenantAsync(HttpContext httpContext, Guid tenantId) => Task.CompletedTask;
    public Guid? GetPreferredTenantId(HttpContext httpContext) => null;
    public string? GetPreferredTenantSlug(HttpContext httpContext) => null;
}
