using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.MultiTenancy;

[TestClass]
public class JwksMultiTenancyTests
{
    private ServiceProvider _serviceProvider = null!;
    private AuthDbContext _db = null!;
    private Guid _tenantAId;
    private Guid _tenantBId;

    [TestInitialize]
    public async Task Setup()
    {
        var services = new ServiceCollection();

        // In-memory database
        services.AddDbContext<AuthDbContext>(options =>
            options.UseInMemoryDatabase($"JwksMultiTenancyTests_{Guid.NewGuid()}"));

        // HybridCache
        services.AddHybridCache();

        // Multi-tenancy services
        services.AddMemoryCache();
        services.AddLogging();
        services.AddScoped<ITenantAccessor, TenantAccessor>();
        services.AddSingleton<IMultiTenancyOptions>(new MultiTenancyStateProvider("default", true));
        services.AddScoped<ITenantResolver, ModeAwareTenantResolver>();

        // KeyStore
        services.AddScoped<IKeyStore, KeyStore>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<AuthDbContext>();

        // Create two test tenants
        _tenantAId = Guid.NewGuid();
        _tenantBId = Guid.NewGuid();

        _db.Tenants.Add(new Tenant
        {
            Id = _tenantAId,
            Slug = "tenant-a",
            Name = "Tenant A",
            Status = TenantStatus.Active,
            IssuerUri = "https://auth.example.com/t/tenant-a"
        });

        _db.Tenants.Add(new Tenant
        {
            Id = _tenantBId,
            Slug = "tenant-b",
            Name = "Tenant B",
            Status = TenantStatus.Active,
            IssuerUri = "https://auth.example.com/t/tenant-b"
        });

        await _db.SaveChangesAsync();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _db?.Dispose();
        _serviceProvider?.Dispose();
    }

    [TestMethod]
    public async Task GetPublicJwksAsync_DifferentTenants_ReturnsDifferentKeys()
    {
        // Arrange - Set context to Tenant A and get/create key
        using var scopeA = _serviceProvider.CreateScope();
        var tenantAccessorA = scopeA.ServiceProvider.GetRequiredService<ITenantAccessor>();
        var keyStoreA = scopeA.ServiceProvider.GetRequiredService<IKeyStore>();

        tenantAccessorA.SetTenant(new TenantContext
        {
            TenantId = _tenantAId,
            Slug = "tenant-a",
            Name = "Tenant A",
            IssuerUri = "https://auth.example.com/t/tenant-a",
            IsMultiTenantMode = true
        });

        // Act - Get active signing key for Tenant A (will create if not exists)
        var keyA = await keyStoreA.GetActiveSigningKeyAsync();
        var jwksA = await keyStoreA.GetPublicJwksAsync();

        // Arrange - Set context to Tenant B and get/create key
        using var scopeB = _serviceProvider.CreateScope();
        var tenantAccessorB = scopeB.ServiceProvider.GetRequiredService<ITenantAccessor>();
        var keyStoreB = scopeB.ServiceProvider.GetRequiredService<IKeyStore>();

        tenantAccessorB.SetTenant(new TenantContext
        {
            TenantId = _tenantBId,
            Slug = "tenant-b",
            Name = "Tenant B",
            IssuerUri = "https://auth.example.com/t/tenant-b",
            IsMultiTenantMode = true
        });

        // Act - Get active signing key for Tenant B (will create if not exists)
        var keyB = await keyStoreB.GetActiveSigningKeyAsync();
        var jwksB = await keyStoreB.GetPublicJwksAsync();

        // Assert - Keys should be different
        Assert.AreNotEqual(keyA.Kid, keyB.Kid, "Tenant A and Tenant B should have different key IDs");
        Assert.HasCount(1, jwksA, "Tenant A should have exactly 1 key");
        Assert.HasCount(1, jwksB, "Tenant B should have exactly 1 key");
        Assert.AreEqual(keyA.Kid, jwksA[0].Kid, "JWKS should contain Tenant A's key");
        Assert.AreEqual(keyB.Kid, jwksB[0].Kid, "JWKS should contain Tenant B's key");
    }

    [TestMethod]
    public async Task GetPublicJwksAsync_WithoutTenantContext_ThrowsException()
    {
        // Arrange - No tenant context set
        using var scope = _serviceProvider.CreateScope();
        var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await keyStore.GetPublicJwksAsync(),
            "GetPublicJwksAsync should throw when tenant context is not set");
    }

    [TestMethod]
    public async Task GetActiveSigningKeyAsync_WithoutTenantContext_ThrowsException()
    {
        // Arrange - No tenant context set
        using var scope = _serviceProvider.CreateScope();
        var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await keyStore.GetActiveSigningKeyAsync(),
            "GetActiveSigningKeyAsync should throw when tenant context is not set");
    }

    [TestMethod]
    public async Task GetPublicJwksAsync_DoesNotIncludePrivateKeyMaterial()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
        var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();

        tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenantAId,
            Slug = "tenant-a",
            Name = "Tenant A",
            IssuerUri = "https://auth.example.com/t/tenant-a",
            IsMultiTenantMode = true
        });

        // Act - Create key and get public JWKS
        await keyStore.GetActiveSigningKeyAsync();
        var jwks = await keyStore.GetPublicJwksAsync();

        // Assert - Public JWKS should not include private key components
        Assert.HasCount(1, jwks);
        var publicKey = jwks[0];
        Assert.IsNull(publicKey.D, "Public JWKS should not include private exponent D");
        Assert.IsNull(publicKey.P, "Public JWKS should not include prime P");
        Assert.IsNull(publicKey.Q, "Public JWKS should not include prime Q");
        Assert.IsNull(publicKey.DP, "Public JWKS should not include DP");
        Assert.IsNull(publicKey.DQ, "Public JWKS should not include DQ");
        Assert.IsNull(publicKey.QI, "Public JWKS should not include QI");
        Assert.IsNotNull(publicKey.N, "Public JWKS should include modulus N");
        Assert.IsNotNull(publicKey.E, "Public JWKS should include exponent E");
    }

    [TestMethod]
    public async Task SigningKeys_InDatabase_AreIsolatedByTenant()
    {
        // Arrange - Create keys for both tenants using the shared DbContext
        var tenantAccessor = _serviceProvider.GetRequiredService<ITenantAccessor>();
        var keyStore = _serviceProvider.GetRequiredService<IKeyStore>();

        // Create key for Tenant A
        tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenantAId,
            Slug = "tenant-a",
            Name = "Tenant A",
            IssuerUri = "https://auth.example.com/t/tenant-a",
            IsMultiTenantMode = true
        });
        await keyStore.GetActiveSigningKeyAsync();

        // Create key for Tenant B
        tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = _tenantBId,
            Slug = "tenant-b",
            Name = "Tenant B",
            IssuerUri = "https://auth.example.com/t/tenant-b",
            IsMultiTenantMode = true
        });
        await keyStore.GetActiveSigningKeyAsync();

        // Act - Query database directly
        var keysInDb = await _db.SigningKeys.ToListAsync();
        var tenantAKeys = keysInDb.Where(k => k.TenantId == _tenantAId).ToList();
        var tenantBKeys = keysInDb.Where(k => k.TenantId == _tenantBId).ToList();

        // Assert
        Assert.HasCount(2, keysInDb, "Should have 2 keys total in database");
        Assert.HasCount(1, tenantAKeys, "Tenant A should have 1 key");
        Assert.HasCount(1, tenantBKeys, "Tenant B should have 1 key");
        Assert.AreNotEqual(tenantAKeys[0].Kid, tenantBKeys[0].Kid, "Keys should have different Kids");
    }

    #region Advanced JWKS Scenarios (Phase 3 - Week 1, Day 2-3)

    [TestMethod]
    public async Task KeyRotation_IndependentPerTenant()
    {
        // Arrange - Create initial keys for both tenants
        var tenantAccessorA = MockTenantAccessor.CreateWithTenant(_tenantAId, "tenant-a");
        var tenantAccessorB = MockTenantAccessor.CreateWithTenant(_tenantBId, "tenant-b");
        
        var keyStoreA = new KeyStore(_db, tenantAccessorA, new TestHybridCache());
        var keyStoreB = new KeyStore(_db, tenantAccessorB, new TestHybridCache());

        var initialKeyA = await keyStoreA.GetActiveSigningKeyAsync();
        var initialKeyB = await keyStoreB.GetActiveSigningKeyAsync();

        Assert.IsNotNull(initialKeyA);
        Assert.IsNotNull(initialKeyB);

        // Act - Manually add a new key for Tenant A (simulate rotation)
        var newKidForA = Guid.NewGuid().ToString("N");
        var newKeyForA = new SigningKey
        {
            Kid = newKidForA,
            Alg = "RS256",
            JwkJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                kty = "RSA",
                kid = newKidForA,
                alg = "RS256",
                n = "test-modulus-new",
                e = "AQAB"
            }),
            TenantId = _tenantAId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.SigningKeys.Add(newKeyForA);
        await _db.SaveChangesAsync();

        // Invalidate cache for Tenant A
        await keyStoreA.InvalidateActiveSigningKeyCacheAsync(_tenantAId);

        // Get updated keys
        var rotatedKeyA = await keyStoreA.GetActiveSigningKeyAsync();
        var unchangedKeyB = await keyStoreB.GetActiveSigningKeyAsync();

        // Assert - Tenant A key rotated, Tenant B unchanged
        Assert.AreNotEqual(initialKeyA.Kid, rotatedKeyA.Kid, "Tenant A key should have rotated");
        Assert.AreEqual(initialKeyB.Kid, unchangedKeyB.Kid, "Tenant B key should remain unchanged");
        Assert.AreEqual(newKidForA, rotatedKeyA.Kid, "Tenant A should use new key");
    }

    [TestMethod]
    public async Task MultiKeyValidation_AfterRotation_BothKeysInJwks()
    {
        // Arrange - Create initial key for Tenant A
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(_tenantAId, "tenant-a");
        var keyStore = new KeyStore(_db, tenantAccessor, new TestHybridCache());

        var oldKey = await keyStore.GetActiveSigningKeyAsync();

        // Add a new key (simulate rotation) - both should appear in JWKS
        var newKeyKid = Guid.NewGuid().ToString("N");
        var newKey = new SigningKey
        {
            Kid = newKeyKid,
            Alg = "RS256",
            JwkJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                kty = "RSA",
                kid = newKeyKid, // Must match the Kid property
                alg = "RS256",
                n = "test-modulus",
                e = "AQAB"
            }),
            TenantId = _tenantAId,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1) // Newer than old key
        };
        _db.SigningKeys.Add(newKey);
        await _db.SaveChangesAsync();

        // Invalidate cache
        await keyStore.InvalidatePublicJwksCacheAsync(_tenantAId);

        // Act - Get JWKS (should contain both keys, not retired)
        var jwks = await keyStore.GetPublicJwksAsync();

        // Assert - Both keys should be present (grace period)
        Assert.IsGreaterThanOrEqualTo(jwks.Count, 2, "JWKS should contain at least 2 keys after rotation (old + new)");
        
        var kids = jwks.Select(k => k.Kid).ToList();
        Assert.Contains(oldKey.Kid, kids, "JWKS should still contain old key (grace period)");
    }

    [TestMethod]
    public async Task RetiredKeys_ExcludedFromJwks()
    {
        // Arrange - Create and retire a key for Tenant A
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(_tenantAId, "tenant-a");
        var keyStore = new KeyStore(_db, tenantAccessor, new TestHybridCache());

        var activeKey = await keyStore.GetActiveSigningKeyAsync();

        // Add a retired key
        var retiredKeyKid = "retired-key";
        var retiredKey = new SigningKey
        {
            Kid = retiredKeyKid,
            Alg = "RS256",
            JwkJson = $"{{\"kty\":\"RSA\",\"kid\":\"{retiredKeyKid}\",\"n\":\"test\",\"e\":\"AQAB\"}}",
            TenantId = _tenantAId,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            RetiredAt = DateTimeOffset.UtcNow.AddDays(-1) // Retired yesterday
        };
        _db.SigningKeys.Add(retiredKey);
        await _db.SaveChangesAsync();

        // Invalidate cache
        await keyStore.InvalidatePublicJwksCacheAsync(_tenantAId);

        // Act - Get JWKS
        var jwks = await keyStore.GetPublicJwksAsync();

        // Assert - Retired key should NOT be in JWKS
        var kids = jwks.Select(k => k.Kid).ToList();
        Assert.DoesNotContain(retiredKey.Kid, kids, "JWKS should not contain retired keys");
        Assert.Contains(activeKey.Kid, kids, "JWKS should contain active key");
    }

    [TestMethod]
    public async Task KeyRotationHistory_MaintainedIndependentlyPerTenant()
    {
        // Arrange - Create multiple keys for each tenant
        var tenantAccessorA = MockTenantAccessor.CreateWithTenant(_tenantAId, "tenant-a");
        var tenantAccessorB = MockTenantAccessor.CreateWithTenant(_tenantBId, "tenant-b");
        
        var keyStoreA = new KeyStore(_db, tenantAccessorA, new TestHybridCache());
        var keyStoreB = new KeyStore(_db, tenantAccessorB, new TestHybridCache());

        // Create initial keys
        await keyStoreA.GetActiveSigningKeyAsync();
        await keyStoreB.GetActiveSigningKeyAsync();

        // Add historical keys for Tenant A
        _db.SigningKeys.Add(new SigningKey
        {
            Kid = "historical-a-1",
            Alg = "RS256",
            JwkJson = "{\"kty\":\"RSA\",\"kid\":\"historical-a-1\",\"n\":\"test1\",\"e\":\"AQAB\"}",
            TenantId = _tenantAId,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        _db.SigningKeys.Add(new SigningKey
        {
            Kid = "historical-a-2",
            Alg = "RS256",
            JwkJson = "{\"kty\":\"RSA\",\"kid\":\"historical-a-2\",\"n\":\"test2\",\"e\":\"AQAB\"}",
            TenantId = _tenantAId,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-5)
        });

        await _db.SaveChangesAsync();

        // Act - Query key history for each tenant
        var tenantAKeyHistory = await _db.SigningKeys
            .Where(k => k.TenantId == _tenantAId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();

        var tenantBKeyHistory = await _db.SigningKeys
            .Where(k => k.TenantId == _tenantBId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();

        // Assert - Tenant A has 3 keys, Tenant B has 1
        Assert.IsGreaterThanOrEqualTo(tenantAKeyHistory.Count, 3, "Tenant A should have at least 3 keys (historical + current)");
        Assert.HasCount(1, tenantBKeyHistory, "Tenant B should have exactly 1 key");

        // Verify all Tenant A keys belong to Tenant A
        Assert.IsTrue(tenantAKeyHistory.All(k => k.TenantId == _tenantAId), "All Tenant A keys should have correct TenantId");
        Assert.IsTrue(tenantBKeyHistory.All(k => k.TenantId == _tenantBId), "All Tenant B keys should have correct TenantId");
    }

    [TestMethod]
    public async Task JwksCache_InvalidatedIndependentlyPerTenant()
    {
        // Arrange - Create keys and get JWKS for both tenants
        var tenantAccessorA = MockTenantAccessor.CreateWithTenant(_tenantAId, "tenant-a");
        var tenantAccessorB = MockTenantAccessor.CreateWithTenant(_tenantBId, "tenant-b");
        
        var keyStoreA = new KeyStore(_db, tenantAccessorA, new TestHybridCache());
        var keyStoreB = new KeyStore(_db, tenantAccessorB, new TestHybridCache());

        // Create initial keys for both tenants
        await keyStoreA.GetActiveSigningKeyAsync();
        await keyStoreB.GetActiveSigningKeyAsync();

        var jwksA1 = await keyStoreA.GetPublicJwksAsync();
        var jwksB1 = await keyStoreB.GetPublicJwksAsync();

        Assert.HasCount(1, jwksA1, "Tenant A should initially have 1 key");
        Assert.HasCount(1, jwksB1, "Tenant B should initially have 1 key");

        // Act - Add a new key for Tenant A only
        var newKeyKid = "new-cache-test-key";
        _db.SigningKeys.Add(new SigningKey
        {
            Kid = newKeyKid,
            Alg = "RS256",
            JwkJson = $"{{\"kty\":\"RSA\",\"kid\":\"{newKeyKid}\",\"n\":\"test\",\"e\":\"AQAB\"}}",
            TenantId = _tenantAId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        // Get JWKS again (TestHybridCache doesn't actually cache, so will see new data)
        var jwksA2 = await keyStoreA.GetPublicJwksAsync();
        var jwksB2 = await keyStoreB.GetPublicJwksAsync();

        // Assert - Tenant A has new key, Tenant B unchanged
        Assert.HasCount(2, jwksA2, "Tenant A should now have 2 keys");
        Assert.HasCount(1, jwksB2, "Tenant B should still have 1 key");
        Assert.IsTrue(jwksA2.Any(k => k.Kid == newKeyKid), "Tenant A JWKS should contain the new key");
        Assert.IsFalse(jwksB2.Any(k => k.Kid == newKeyKid), "Tenant B JWKS should not contain Tenant A's key");
    }

    [TestMethod]
    public async Task CrossTenant_KeyLookup_Fails_KeyNotFound()
    {
        // Arrange - Create keys for both tenants
        var tenantAccessorA = MockTenantAccessor.CreateWithTenant(_tenantAId, "tenant-a", issuerUri: "https://auth.example.com/t/tenant-a");
        var tenantAccessorB = MockTenantAccessor.CreateWithTenant(_tenantBId, "tenant-b", issuerUri: "https://auth.example.com/t/tenant-b");
        
        var keyStoreA = new KeyStore(_db, tenantAccessorA, new TestHybridCache());
        var keyStoreB = new KeyStore(_db, tenantAccessorB, new TestHybridCache());

        var keyA = await keyStoreA.GetActiveSigningKeyAsync();
        var keyB = await keyStoreB.GetActiveSigningKeyAsync();

        // Create JwtService and TokenValidator
        var jwtServiceA = TestJwtServiceFactory.Create(keyStoreA);
        var tokenValidatorB = TestTokenValidatorFactory.Create(keyStoreB);

        // Act - Create token in Tenant A with Tenant A's kid
        var claims = new[] { new System.Security.Claims.Claim("sub", "user123") };
        var tokenFromA = await jwtServiceA.CreateJwtAsync(
            issuer: "https://auth.example.com/t/tenant-a",
            audience: "test-client",
            claims: claims,
            expires: DateTimeOffset.UtcNow.AddHours(1)
        ).ConfigureAwait(false);

        // Try to validate token from A using Tenant B's keystore
        var (valid, principal, error) = await tokenValidatorB.ValidateAsync(
            tokenFromA,
            issuer: "https://auth.example.com/t/tenant-a" // Even with correct issuer
        );

        // Assert - Validation should fail (Tenant B doesn't have Tenant A's key)
        Assert.IsFalse(valid, "Token from Tenant A should fail validation in Tenant B context");
        Assert.IsNotNull(error, "Error should be present");

        // Verify the kid from token A is not in Tenant B's JWKS
        var jwksB = await keyStoreB.GetPublicJwksAsync();
        var kidB = jwksB.Select(k => k.Kid).ToList();
        Assert.DoesNotContain(kidB, keyA.Kid, "Tenant B JWKS should not contain Tenant A's key");
    }

    [TestMethod]
    public async Task SigningKeyRetrieval_UsesCorrectTenantContext()
    {
        // Arrange - Create keys for both tenants
        var tenantAccessorA = MockTenantAccessor.CreateWithTenant(_tenantAId, "tenant-a");
        var keyStoreA = new KeyStore(_db, tenantAccessorA, new TestHybridCache());
        await keyStoreA.GetActiveSigningKeyAsync();

        var tenantAccessorB = MockTenantAccessor.CreateWithTenant(_tenantBId, "tenant-b");
        var keyStoreB = new KeyStore(_db, tenantAccessorB, new TestHybridCache());
        await keyStoreB.GetActiveSigningKeyAsync();

        // Act - Switch tenant context and verify key retrieval
        var keyRetrievedForA = await keyStoreA.GetActiveSigningKeyAsync();
        var keyRetrievedForB = await keyStoreB.GetActiveSigningKeyAsync();

        // Assert - Each tenant gets its own key
        Assert.AreNotEqual(keyRetrievedForA.Kid, keyRetrievedForB.Kid, "Different tenants should get different keys");

        // Verify keys in database have correct TenantId
        var keyAFromDb = await _db.SigningKeys.FirstAsync(k => k.Kid == keyRetrievedForA.Kid);
        var keyBFromDb = await _db.SigningKeys.FirstAsync(k => k.Kid == keyRetrievedForB.Kid);

        Assert.AreEqual(_tenantAId, keyAFromDb.TenantId, "Key A should belong to Tenant A");
        Assert.AreEqual(_tenantBId, keyBFromDb.TenantId, "Key B should belong to Tenant B");
    }

    [TestMethod]
    public async Task JwksEndpoint_AfterKeyRotation_ContainsMultipleKeys()
    {
        // Arrange - Create initial key
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(_tenantAId, "tenant-a");
        var keyStore = new KeyStore(_db, tenantAccessor, new TestHybridCache());

        var key1 = await keyStore.GetActiveSigningKeyAsync();

        // Simulate key rotation by adding new keys with different timestamps
        var key2 = new SigningKey
        {
            Kid = Guid.NewGuid().ToString("N"),
            Alg = "RS256",
            JwkJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                kty = "RSA",
                kid = Guid.NewGuid().ToString("N"),
                alg = "RS256",
                n = "test-n2",
                e = "AQAB"
            }),
            TenantId = _tenantAId,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };

        var key3 = new SigningKey
        {
            Kid = Guid.NewGuid().ToString("N"),
            Alg = "RS256",
            JwkJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                kty = "RSA",
                kid = Guid.NewGuid().ToString("N"),
                alg = "RS256",
                n = "test-n3",
                e = "AQAB"
            }),
            TenantId = _tenantAId,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(20)
        };

        _db.SigningKeys.AddRange(key2, key3);
        await _db.SaveChangesAsync();

        // Invalidate cache
        await keyStore.InvalidatePublicJwksCacheAsync(_tenantAId);

        // Act - Get JWKS
        var jwks = await keyStore.GetPublicJwksAsync();

        // Assert - Should contain multiple keys (all non-retired)
        Assert.IsGreaterThanOrEqualTo(jwks.Count, 3, $"JWKS should contain at least 3 keys after rotation, but has {jwks.Count}");

        // Verify all keys belong to Tenant A
        var keysInDb = await _db.SigningKeys
            .Where(k => k.TenantId == _tenantAId && k.RetiredAt == null)
            .ToListAsync();
        
        Assert.HasCount(keysInDb.Count, jwks, "JWKS count should match non-retired keys in DB");
    }

    #endregion
}



