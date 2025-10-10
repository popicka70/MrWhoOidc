using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.UnitTests.Testing;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Tests for JWKS endpoint tenant filtering to ensure each tenant only sees their own signing keys.
/// </summary>
[TestClass]
public class TenantJwksEndpointTests
{
    private static readonly Guid Tenant1Id = new("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Tenant2Id = new("20000000-0000-0000-0000-000000000002");

    [TestMethod]
    public async Task GetPublicJwksAsync_Returns_Only_Current_Tenant_Keys()
    {
        // Arrange
        var dbName = "jwks-tenant-filter-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddHybridCache();
        services.AddScoped<IKeyStore, KeyStore>();
        services.AddScoped<ITenantAccessor>(sp =>
        {
            var db = sp.GetRequiredService<AuthDbContext>();
            return new TestTenantAccessor(db, Tenant1Id, null);
        });

        var provider = services.BuildServiceProvider();

        // Seed database with two tenants and their keys
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            // Create tenants
            db.Tenants.Add(new Tenant
            {
                Id = Tenant1Id,
                Slug = "tenant1",
                Name = "Tenant 1",
                IssuerUri = "https://auth.example.com/t/tenant1",
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });

            db.Tenants.Add(new Tenant
            {
                Id = Tenant2Id,
                Slug = "tenant2",
                Name = "Tenant 2",
                IssuerUri = "https://auth.example.com/t/tenant2",
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });

            // Create signing keys for tenant1
            db.SigningKeys.Add(new SigningKey
            {
                Kid = "tenant1-key1",
                Alg = "RS256",
                JwkJson = "{\"kty\":\"RSA\",\"kid\":\"tenant1-key1\",\"alg\":\"RS256\",\"n\":\"t1k1n\",\"e\":\"AQAB\"}",
                TenantId = Tenant1Id,
                CreatedAt = DateTimeOffset.UtcNow
            });

            db.SigningKeys.Add(new SigningKey
            {
                Kid = "tenant1-key2",
                Alg = "RS256",
                JwkJson = "{\"kty\":\"RSA\",\"kid\":\"tenant1-key2\",\"alg\":\"RS256\",\"n\":\"t1k2n\",\"e\":\"AQAB\"}",
                TenantId = Tenant1Id,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            });

            // Create signing keys for tenant2
            db.SigningKeys.Add(new SigningKey
            {
                Kid = "tenant2-key1",
                Alg = "RS256",
                JwkJson = "{\"kty\":\"RSA\",\"kid\":\"tenant2-key1\",\"alg\":\"RS256\",\"n\":\"t2k1n\",\"e\":\"AQAB\"}",
                TenantId = Tenant2Id,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync();
        }

        // Act & Assert - Tenant 1 should only see tenant1 keys
        using (var scope = provider.CreateScope())
        {
            var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>() as TestTenantAccessor;
            tenantAccessor!.SetTenant(new TenantContext
            {
                TenantId = Tenant1Id,
                Slug = "tenant1",
                Name = "Tenant 1",
                IssuerUri = "https://auth.example.com/t/tenant1",
                IsMultiTenantMode = true
            });

            var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
            var jwks = await keyStore.GetPublicJwksAsync();

            Assert.HasCount(2, jwks, "Tenant1 should see exactly 2 keys");
            Assert.IsTrue(jwks.All(k => k.Kid.StartsWith("tenant1-")), "All keys should belong to tenant1");
            Assert.IsFalse(jwks.Any(k => k.Kid.StartsWith("tenant2-")), "No tenant2 keys should be visible");
        }

        // Act & Assert - Tenant 2 should only see tenant2 keys
        using (var scope = provider.CreateScope())
        {
            var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>() as TestTenantAccessor;
            tenantAccessor!.SetTenant(new TenantContext
            {
                TenantId = Tenant2Id,
                Slug = "tenant2",
                Name = "Tenant 2",
                IssuerUri = "https://auth.example.com/t/tenant2",
                IsMultiTenantMode = true
            });

            var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
            var jwks = await keyStore.GetPublicJwksAsync();

            Assert.HasCount(1, jwks, "Tenant2 should see exactly 1 key");
            Assert.IsTrue(jwks.All(k => k.Kid.StartsWith("tenant2-")), "All keys should belong to tenant2");
            Assert.IsFalse(jwks.Any(k => k.Kid.StartsWith("tenant1-")), "No tenant1 keys should be visible");
        }
    }

    [TestMethod]
    public async Task GetPublicJwksAsync_Excludes_Retired_Keys()
    {
        // Arrange
        var dbName = "jwks-retired-filter-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddHybridCache();
        services.AddScoped<IKeyStore, KeyStore>();
        services.AddScoped<ITenantAccessor>(sp =>
        {
            var db = sp.GetRequiredService<AuthDbContext>();
            return new TestTenantAccessor(db, Tenant1Id, null);
        });

        var provider = services.BuildServiceProvider();

        // Seed database with tenant and keys (one active, one retired)
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            db.Tenants.Add(new Tenant
            {
                Id = Tenant1Id,
                Slug = "tenant1",
                Name = "Tenant 1",
                IssuerUri = "https://auth.example.com/t/tenant1",
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });

            // Active key
            db.SigningKeys.Add(new SigningKey
            {
                Kid = "active-key",
                Alg = "RS256",
                JwkJson = "{\"kty\":\"RSA\",\"kid\":\"active-key\",\"alg\":\"RS256\",\"n\":\"active\",\"e\":\"AQAB\"}",
                TenantId = Tenant1Id,
                CreatedAt = DateTimeOffset.UtcNow,
                RetiredAt = null
            });

            // Retired key
            db.SigningKeys.Add(new SigningKey
            {
                Kid = "retired-key",
                Alg = "RS256",
                JwkJson = "{\"kty\":\"RSA\",\"kid\":\"retired-key\",\"alg\":\"RS256\",\"n\":\"retired\",\"e\":\"AQAB\"}",
                TenantId = Tenant1Id,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-7),
                RetiredAt = DateTimeOffset.UtcNow.AddDays(-1)
            });

            await db.SaveChangesAsync();
        }

        // Act
        using (var scope = provider.CreateScope())
        {
            var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>() as TestTenantAccessor;
            tenantAccessor!.SetTenant(new TenantContext
            {
                TenantId = Tenant1Id,
                Slug = "tenant1",
                Name = "Tenant 1",
                IssuerUri = "https://auth.example.com/t/tenant1",
                IsMultiTenantMode = true
            });

            var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
            var jwks = await keyStore.GetPublicJwksAsync();

            // Assert
            Assert.HasCount(1, jwks, "Should only return active (non-retired) keys");
            Assert.AreEqual("active-key", jwks[0].Kid);
        }
    }

    [TestMethod]
    public async Task GetPublicJwksAsync_Strips_Private_Key_Material()
    {
        // Arrange
        var dbName = "jwks-private-strip-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddHybridCache();
        services.AddScoped<IKeyStore, KeyStore>();
        services.AddScoped<ITenantAccessor>(sp =>
        {
            var db = sp.GetRequiredService<AuthDbContext>();
            return new TestTenantAccessor(db, Tenant1Id, null);
        });

        var provider = services.BuildServiceProvider();

        // Seed database with tenant and key containing private material
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            db.Tenants.Add(new Tenant
            {
                Id = Tenant1Id,
                Slug = "tenant1",
                Name = "Tenant 1",
                IssuerUri = "https://auth.example.com/t/tenant1",
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });

            // Key with private material
            db.SigningKeys.Add(new SigningKey
            {
                Kid = "test-key",
                Alg = "RS256",
                JwkJson = "{\"kty\":\"RSA\",\"kid\":\"test-key\",\"alg\":\"RS256\",\"n\":\"public-n\",\"e\":\"AQAB\",\"d\":\"private-d\",\"p\":\"private-p\",\"q\":\"private-q\",\"dp\":\"private-dp\",\"dq\":\"private-dq\",\"qi\":\"private-qi\"}",
                TenantId = Tenant1Id,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync();
        }

        // Act
        using (var scope = provider.CreateScope())
        {
            var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>() as TestTenantAccessor;
            tenantAccessor!.SetTenant(new TenantContext
            {
                TenantId = Tenant1Id,
                Slug = "tenant1",
                Name = "Tenant 1",
                IssuerUri = "https://auth.example.com/t/tenant1",
                IsMultiTenantMode = true
            });

            var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
            var jwks = await keyStore.GetPublicJwksAsync();

            // Assert
            Assert.HasCount(1, jwks);
            var publicKey = jwks[0];

            Assert.AreEqual("test-key", publicKey.Kid);
            Assert.AreEqual("public-n", publicKey.N);
            Assert.AreEqual("AQAB", publicKey.E);

            // Verify private material is stripped
            Assert.IsNull(publicKey.D, "Private exponent D should be null");
            Assert.IsNull(publicKey.P, "Private prime P should be null");
            Assert.IsNull(publicKey.Q, "Private prime Q should be null");
            Assert.IsNull(publicKey.DP, "Private DP should be null");
            Assert.IsNull(publicKey.DQ, "Private DQ should be null");
            Assert.IsNull(publicKey.QI, "Private QI should be null");
        }
    }

    [TestMethod]
    public async Task GetPublicJwksAsync_Returns_Keys_Ordered_By_Created_Descending()
    {
        // Arrange
        var dbName = "jwks-ordering-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddHybridCache();
        services.AddScoped<IKeyStore, KeyStore>();
        services.AddScoped<ITenantAccessor>(sp =>
        {
            var db = sp.GetRequiredService<AuthDbContext>();
            return new TestTenantAccessor(db, Tenant1Id, null);
        });

        var provider = services.BuildServiceProvider();

        // Seed database with multiple keys at different times
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            db.Tenants.Add(new Tenant
            {
                Id = Tenant1Id,
                Slug = "tenant1",
                Name = "Tenant 1",
                IssuerUri = "https://auth.example.com/t/tenant1",
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });

            // Add keys in non-chronological order
            db.SigningKeys.Add(new SigningKey
            {
                Kid = "key-2",
                Alg = "RS256",
                JwkJson = "{\"kty\":\"RSA\",\"kid\":\"key-2\",\"alg\":\"RS256\",\"n\":\"n2\",\"e\":\"AQAB\"}",
                TenantId = Tenant1Id,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2)
            });

            db.SigningKeys.Add(new SigningKey
            {
                Kid = "key-1",
                Alg = "RS256",
                JwkJson = "{\"kty\":\"RSA\",\"kid\":\"key-1\",\"alg\":\"RS256\",\"n\":\"n1\",\"e\":\"AQAB\"}",
                TenantId = Tenant1Id,
                CreatedAt = DateTimeOffset.UtcNow // Most recent
            });

            db.SigningKeys.Add(new SigningKey
            {
                Kid = "key-3",
                Alg = "RS256",
                JwkJson = "{\"kty\":\"RSA\",\"kid\":\"key-3\",\"alg\":\"RS256\",\"n\":\"n3\",\"e\":\"AQAB\"}",
                TenantId = Tenant1Id,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-5)
            });

            await db.SaveChangesAsync();
        }

        // Act
        using (var scope = provider.CreateScope())
        {
            var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>() as TestTenantAccessor;
            tenantAccessor!.SetTenant(new TenantContext
            {
                TenantId = Tenant1Id,
                Slug = "tenant1",
                Name = "Tenant 1",
                IssuerUri = "https://auth.example.com/t/tenant1",
                IsMultiTenantMode = true
            });

            var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();
            var jwks = await keyStore.GetPublicJwksAsync();

            // Assert - keys should be ordered by CreatedAt descending (newest first)
            Assert.HasCount(3, jwks);
            Assert.AreEqual("key-1", jwks[0].Kid, "Newest key should be first");
            Assert.AreEqual("key-2", jwks[1].Kid, "Second newest key should be second");
            Assert.AreEqual("key-3", jwks[2].Kid, "Oldest key should be last");
        }
    }

    [TestMethod]
    public async Task GetPublicJwksAsync_Throws_When_No_Tenant_Context()
    {
        // Arrange
        var dbName = "jwks-no-tenant-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddHybridCache();
        services.AddScoped<IKeyStore, KeyStore>();
        services.AddScoped<ITenantAccessor>(sp =>
        {
            var db = sp.GetRequiredService<AuthDbContext>();
            return new TestTenantAccessor(db, Guid.Empty, null);
        });

        var provider = services.BuildServiceProvider();

        // Act & Assert
        using (var scope = provider.CreateScope())
        {
            var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await keyStore.GetPublicJwksAsync());
        }
    }
}
