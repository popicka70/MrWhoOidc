using Microsoft.EntityFrameworkCore;
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
        
        // Multi-tenancy services
        services.AddMemoryCache();
        services.AddScoped<ITenantAccessor, TenantAccessor>();
        services.AddSingleton<IMultiTenancyOptions>(new MultiTenancyOptions 
        { 
            Enabled = true, 
            DefaultTenantSlug = "default" 
        });
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
    public void Cleanup()
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
        Assert.AreEqual(1, jwksA.Count, "Tenant A should have exactly 1 key");
        Assert.AreEqual(1, jwksB.Count, "Tenant B should have exactly 1 key");
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
        Assert.AreEqual(1, jwks.Count);
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
        Assert.AreEqual(2, keysInDb.Count, "Should have 2 keys total in database");
        Assert.AreEqual(1, tenantAKeys.Count, "Tenant A should have 1 key");
        Assert.AreEqual(1, tenantBKeys.Count, "Tenant B should have 1 key");
        Assert.AreNotEqual(tenantAKeys[0].Kid, tenantBKeys[0].Kid, "Keys should have different Kids");
    }
}
