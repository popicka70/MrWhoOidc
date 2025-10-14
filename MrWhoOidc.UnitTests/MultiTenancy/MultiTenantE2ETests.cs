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
        var clientStore = new ClientStore(_db, new DummyHasher(), tenantAccessor, new TestHybridCache());

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
        var clientStore = new ClientStore(_db, new DummyHasher(), tenantAccessor, new TestHybridCache());

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
        var store1 = new ClientStore(_db, new DummyHasher(), accessor1, new TestHybridCache());
        var result1 = await store1.FindByClientIdAsync(sameClientId);

        // Assert - should get tenant 1's version
        Assert.IsNotNull(result1);
        Assert.AreEqual("Tenant 1 SPA", result1.ClientName);
        Assert.IsTrue(result1.RequirePkce, "Should have tenant 1 settings");

        // Act - retrieve client from tenant 2 context
        var accessor2 = MockTenantAccessor.CreateWithTenant(_tenant2Id, "contoso");
        var store2 = new ClientStore(_db, new DummyHasher(), accessor2, new TestHybridCache());
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

    #region Authorization Flow E2E Tests (Phase 3 - Week 1)

    [TestMethod]
    public async Task FullAuthFlow_TokenIssuer_MatchesTenantIssuer_Tenant1()
    {
        // Arrange - Set up user and client for tenant 1
        var user1 = new User
        {
            Username = "alice@acme.com",
            Email = "alice@acme.com",
            Name = "Alice",
            TenantId = _tenant1Id,
            PasswordHash = "hash"
        };
        _db.Users.Add(user1);

        var client1 = await _db.Clients.FirstAsync(c => c.TenantId == _tenant1Id);
        
        // Add scope assignments
        var openidScope = new Scope { Name = "openid", Description = "OpenID Connect" };
        var profileScope = new Scope { Name = "profile", Description = "Profile" };
        _db.Scopes.AddRange(openidScope, profileScope);
        _db.ClientScopes.AddRange(
            new ClientScope { ClientId = client1.Id, ScopeName = "openid" },
            new ClientScope { ClientId = client1.Id, ScopeName = "profile" }
        );
        await _db.SaveChangesAsync();

        // Create authorization code
        var authCode = new AuthorizationCode
        {
            Code = "test-code-tenant1",
            ClientId = client1.ClientId,
            UserId = user1.Id,
            RedirectUri = "https://acme-app.com/callback",
            ScopesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "openid", "profile" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            TenantId = _tenant1Id
        };
        _db.AuthorizationCodes.Add(authCode);
        await _db.SaveChangesAsync();

        // Set up services with tenant 1 context
        var tenant1Accessor = MockTenantAccessor.CreateWithTenant(_tenant1Id, "acme", "Acme Corporation", "https://localhost:5001/t/acme");
        var keyStore = new KeyStore(_db, tenant1Accessor, new TestHybridCache());
        var jwtService = new JwtService(keyStore);

        // Act - Create ID token using JwtService
        var claims = new[]
        {
            new System.Security.Claims.Claim("sub", user1.Id.ToString()),
            new System.Security.Claims.Claim("email", user1.Email)
        };
        var idToken = jwtService.CreateJwt(
            issuer: "https://localhost:5001/t/acme",
            audience: client1.ClientId,
            claims: claims,
            expires: DateTimeOffset.UtcNow.AddHours(1)
        );

        // Assert - Parse token and verify issuer
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(idToken);

        Assert.AreEqual("https://localhost:5001/t/acme", jwt.Issuer, "Token issuer should match tenant 1 issuer");
        Assert.IsTrue(jwt.Claims.Any(c => c.Type == "email" && c.Value == "alice@acme.com"));
    }

    [TestMethod]
    public async Task FullAuthFlow_TokenIssuer_MatchesTenantIssuer_Tenant2()
    {
        // Arrange - Set up user and client for tenant 2
        var user2 = new User
        {
            Username = "bob@contoso.com",
            Email = "bob@contoso.com",
            Name = "Bob",
            TenantId = _tenant2Id,
            PasswordHash = "hash"
        };
        _db.Users.Add(user2);

        var client2 = await _db.Clients.FirstAsync(c => c.TenantId == _tenant2Id);
        
        // Add scope assignments
        var openidScope = await _db.Scopes.FirstOrDefaultAsync(s => s.Name == "openid");
        if (openidScope == null)
        {
            openidScope = new Scope { Name = "openid", Description = "OpenID Connect" };
            _db.Scopes.Add(openidScope);
        }
        _db.ClientScopes.Add(new ClientScope { ClientId = client2.Id, ScopeName = "openid" });
        await _db.SaveChangesAsync();

        // Set up services with tenant 2 context
        var tenant2Accessor = MockTenantAccessor.CreateWithTenant(_tenant2Id, "contoso", "Contoso Ltd", "https://localhost:5001/t/contoso");
        var keyStore = new KeyStore(_db, tenant2Accessor, new TestHybridCache());
        var jwtService = new JwtService(keyStore);

        // Act - Create ID token
        var claims = new[]
        {
            new System.Security.Claims.Claim("sub", user2.Id.ToString()),
            new System.Security.Claims.Claim("email", user2.Email)
        };
        var idToken = jwtService.CreateJwt(
            issuer: "https://localhost:5001/t/contoso",
            audience: client2.ClientId,
            claims: claims,
            expires: DateTimeOffset.UtcNow.AddHours(1)
        );

        // Assert - Verify issuer is different from tenant 1
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(idToken);

        Assert.AreEqual("https://localhost:5001/t/contoso", jwt.Issuer, "Token issuer should match tenant 2 issuer");
        Assert.AreNotEqual("https://localhost:5001/t/acme", jwt.Issuer, "Token issuer should differ from tenant 1");
    }

    [TestMethod]
    public async Task CrossTenant_TokenValidation_Fails_IssuerMismatch()
    {
        // Arrange - Create token in tenant 1
        var user1 = await _db.Users.FirstAsync(u => u.TenantId == _tenant1Id);
        var client1 = await _db.Clients.FirstAsync(c => c.TenantId == _tenant1Id);

        var tenant1Accessor = MockTenantAccessor.CreateWithTenant(_tenant1Id, "acme", issuerUri: "https://localhost:5001/t/acme");
        var keyStore1 = new KeyStore(_db, tenant1Accessor, new TestHybridCache());
        var jwtService1 = new JwtService(keyStore1);

        var claims = new[] { new System.Security.Claims.Claim("sub", user1.Id.ToString()) };
        var tenant1Token = jwtService1.CreateJwt(
            issuer: "https://localhost:5001/t/acme",
            audience: client1.ClientId,
            claims: claims,
            expires: DateTimeOffset.UtcNow.AddHours(1)
        );

        // Act - Try to validate tenant 1 token in tenant 2 context
        var tenant2Accessor = MockTenantAccessor.CreateWithTenant(_tenant2Id, "contoso", issuerUri: "https://localhost:5001/t/contoso");
        var keyStore2 = new KeyStore(_db, tenant2Accessor, new TestHybridCache());
        var validator2 = new TokenValidator(keyStore2);

        var (valid, principal, error) = validator2.Validate(tenant1Token, "https://localhost:5001/t/acme");

        // Assert - Validation should fail (tenant 2 doesn't have tenant 1's keys)
        Assert.IsFalse(valid, "Token from tenant 1 should fail validation in tenant 2 context");
        Assert.IsNotNull(error, "Error message should be present");
    }

    [TestMethod]
    public async Task AuthorizationCode_IsolatedByTenant()
    {
        // Arrange - Create auth codes for both tenants
        var user1 = await _db.Users.FirstAsync(u => u.TenantId == _tenant1Id);
        var user2 = await _db.Users.FirstAsync(u => u.TenantId == _tenant2Id);
        var client1 = await _db.Clients.FirstAsync(c => c.TenantId == _tenant1Id);
        var client2 = await _db.Clients.FirstAsync(c => c.TenantId == _tenant2Id);

        var code1 = new AuthorizationCode
        {
            Code = "code-tenant1",
            ClientId = client1.ClientId,
            UserId = user1.Id,
            RedirectUri = "https://app1.com/callback",
            ScopesJson = "[]",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            TenantId = _tenant1Id
        };

        var code2 = new AuthorizationCode
        {
            Code = "code-tenant2",
            ClientId = client2.ClientId,
            UserId = user2.Id,
            RedirectUri = "https://app2.com/callback",
            ScopesJson = "[]",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            TenantId = _tenant2Id
        };

        _db.AuthorizationCodes.AddRange(code1, code2);
        await _db.SaveChangesAsync();

        // Act - Query codes with tenant filter
        var tenant1Codes = await _db.AuthorizationCodes
            .Where(c => c.TenantId == _tenant1Id)
            .ToListAsync();

        var tenant2Codes = await _db.AuthorizationCodes
            .Where(c => c.TenantId == _tenant2Id)
            .ToListAsync();

        // Assert
        Assert.HasCount(1, tenant1Codes, "Tenant 1 should have 1 auth code");
        Assert.HasCount(1, tenant2Codes, "Tenant 2 should have 1 auth code");
        Assert.AreEqual("code-tenant1", tenant1Codes[0].Code);
        Assert.AreEqual("code-tenant2", tenant2Codes[0].Code);

        // Verify no overlap
        Assert.IsFalse(tenant1Codes.Any(c => c.Code == "code-tenant2"), "Tenant 1 should not see tenant 2's code");
        Assert.IsFalse(tenant2Codes.Any(c => c.Code == "code-tenant1"), "Tenant 2 should not see tenant 1's code");
    }

    [TestMethod]
    public async Task SigningKeys_IsolatedByTenant_DifferentKids()
    {
        // Arrange - Get keys for both tenants
        var tenant1Accessor = MockTenantAccessor.CreateWithTenant(_tenant1Id, "acme");
        var tenant2Accessor = MockTenantAccessor.CreateWithTenant(_tenant2Id, "contoso");

        var keyStore1 = new KeyStore(_db, tenant1Accessor, new TestHybridCache());
        var keyStore2 = new KeyStore(_db, tenant2Accessor, new TestHybridCache());

        // Act - Generate keys for both tenants
        var key1 = await keyStore1.GetActiveSigningKeyAsync();
        var key2 = await keyStore2.GetActiveSigningKeyAsync();

        // Assert - Keys should have different key IDs
        Assert.IsNotNull(key1);
        Assert.IsNotNull(key2);
        Assert.AreNotEqual(key1.Kid, key2.Kid, "Different tenants should have different signing keys");

        // Verify keys are stored with correct tenant ID
        var keysInDb = await _db.SigningKeys.ToListAsync();
        var tenant1Keys = keysInDb.Where(k => k.TenantId == _tenant1Id).ToList();
        var tenant2Keys = keysInDb.Where(k => k.TenantId == _tenant2Id).ToList();

        Assert.IsNotEmpty(tenant1Keys, "Tenant 1 should have signing keys");
        Assert.IsNotEmpty(tenant2Keys, "Tenant 2 should have signing keys");
        Assert.IsTrue(tenant1Keys.All(k => k.TenantId == _tenant1Id), "All tenant 1 keys should have correct tenant ID");
        Assert.IsTrue(tenant2Keys.All(k => k.TenantId == _tenant2Id), "All tenant 2 keys should have correct tenant ID");
    }

    [TestMethod]
    public async Task JWKS_Endpoint_ReturnsOnlyTenantKeys()
    {
        // Arrange - Generate keys for both tenants
        var tenant1Accessor = MockTenantAccessor.CreateWithTenant(_tenant1Id, "acme");
        var tenant2Accessor = MockTenantAccessor.CreateWithTenant(_tenant2Id, "contoso");

        var keyStore1 = new KeyStore(_db, tenant1Accessor, new TestHybridCache());
        var keyStore2 = new KeyStore(_db, tenant2Accessor, new TestHybridCache());

        await keyStore1.GetActiveSigningKeyAsync();
        await keyStore2.GetActiveSigningKeyAsync();

        // Act - Get JWKS for each tenant
        var jwks1 = await keyStore1.GetPublicJwksAsync();
        var jwks2 = await keyStore2.GetPublicJwksAsync();

        // Assert - Each JWKS should only contain its own tenant's keys
        Assert.IsNotEmpty(jwks1, "Tenant 1 JWKS should contain keys");
        Assert.IsNotEmpty(jwks2, "Tenant 2 JWKS should contain keys");

        var kidSet1 = jwks1.Select(k => k.Kid).ToHashSet();
        var kidSet2 = jwks2.Select(k => k.Kid).ToHashSet();

        // Verify no overlap between tenant JWKS
        Assert.IsFalse(kidSet1.Overlaps(kidSet2), "Tenant JWKS should not contain keys from other tenants");
    }

    [TestMethod]
    public async Task Token_Kid_Header_MatchesTenantKey()
    {
        // Arrange
        var tenant1Accessor = MockTenantAccessor.CreateWithTenant(_tenant1Id, "acme", issuerUri: "https://localhost:5001/t/acme");
        var keyStore = new KeyStore(_db, tenant1Accessor, new TestHybridCache());
        var jwtService = new JwtService(keyStore);

        // Get the active signing key to know its kid
        var signingKey = await keyStore.GetActiveSigningKeyAsync();
        var expectedKid = signingKey.Kid;

        // Act - Create token
        var claims = new[] { new System.Security.Claims.Claim("sub", "user123") };
        var token = jwtService.CreateJwt(
            issuer: "https://localhost:5001/t/acme",
            audience: "test-client",
            claims: claims,
            expires: DateTimeOffset.UtcNow.AddHours(1)
        );

        // Assert - Verify kid in token header
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        Assert.AreEqual(expectedKid, jwt.Header.Kid, "Token kid should match tenant's active signing key");
    }

    #endregion
}

