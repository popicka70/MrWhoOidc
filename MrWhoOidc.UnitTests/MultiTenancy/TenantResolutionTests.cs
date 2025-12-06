using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.UnitTests.MultiTenancy;

[TestClass]
public class TenantResolutionTests
{
    private ServiceProvider _serviceProvider = null!;
    private AuthDbContext _db = null!;
    private Guid _defaultTenantId;
    private Guid _acmeTenantId;
    private Guid _contosoTenantId;

    [TestInitialize]
    public async Task Setup()
    {
        var services = new ServiceCollection();

        // In-memory database for testing - register as singleton so all services share the same instance
        services.AddDbContext<AuthDbContext>(options =>
            options.UseInMemoryDatabase(databaseName: $"TenantResolutionTestDb_{Guid.NewGuid()}"),
            ServiceLifetime.Singleton);

        // Memory cache
        services.AddMemoryCache();

        // Logging
        services.AddLogging();

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

        // Seed test tenants
        _defaultTenantId = new Guid("00000000-0000-0000-0000-000000000001");
        _acmeTenantId = Guid.NewGuid();
        _contosoTenantId = Guid.NewGuid();

        _db.Tenants.AddRange(
            new Tenant
            {
                Id = _defaultTenantId,
                Slug = "default",
                Name = "Default Tenant",
                IssuerUri = "https://localhost:5001",
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new Tenant
            {
                Id = _acmeTenantId,
                Slug = "acme",
                Name = "Acme Corporation",
                IssuerUri = "https://localhost:5001/t/acme",
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new Tenant
            {
                Id = _contosoTenantId,
                Slug = "contoso",
                Name = "Contoso Ltd",
                IssuerUri = "https://localhost:5001/t/contoso",
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            }
        );

        await _db.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        _serviceProvider.Dispose();
    }

    [TestMethod]
    public async Task ResolveTenantAsync_MultiTenantMode_WithValidSlug_ReturnsCorrectTenant()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act
        var result = await resolver.ResolveTenantAsync("/t/acme/authorize");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(_acmeTenantId, result.TenantId);
        Assert.AreEqual("acme", result.Slug);
        Assert.AreEqual("Acme Corporation", result.Name);
        Assert.AreEqual("https://localhost:5001/t/acme", result.IssuerUri);
        Assert.IsTrue(result.IsMultiTenantMode);
    }

    [TestMethod]
    public async Task ResolveTenantAsync_MultiTenantMode_WithDifferentSlug_ReturnsDifferentTenant()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act
        var result = await resolver.ResolveTenantAsync("/t/contoso/.well-known/openid-configuration");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(_contosoTenantId, result.TenantId);
        Assert.AreEqual("contoso", result.Slug);
        Assert.AreEqual("Contoso Ltd", result.Name);
        Assert.IsTrue(result.IsMultiTenantMode);
    }

    [TestMethod]
    public async Task ResolveTenantAsync_MultiTenantMode_WithInvalidSlug_ReturnsNull()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act
        var result = await resolver.ResolveTenantAsync("/t/nonexistent/authorize");

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ResolveTenantAsync_MultiTenantMode_WithoutTenantPrefix_FallsBackToDefaultTenant()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act
        var result = await resolver.ResolveTenantAsync("/authorize");

        // Assert
        Assert.IsNotNull(result, "Should fall back to default tenant for backward compatibility");
        Assert.AreEqual(_defaultTenantId, result.TenantId);
        Assert.AreEqual("default", result.Slug);
    }

    [TestMethod]
    public async Task ResolveTenantAsync_MultiTenantMode_CaseInsensitiveSlug_ReturnsCorrectTenant()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - uppercase slug
        var result = await resolver.ResolveTenantAsync("/t/ACME/authorize");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(_acmeTenantId, result.TenantId);
        Assert.AreEqual("acme", result.Slug);
    }

    [TestMethod]
    public async Task ResolveTenantAsync_SingleTenantMode_ReturnsDefaultTenant()
    {
        // Arrange - single-tenant mode
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(options =>
            options.UseInMemoryDatabase(databaseName: $"SingleTenantTestDb_{Guid.NewGuid()}"),
            ServiceLifetime.Singleton);
        services.AddMemoryCache();
        services.AddLogging();

        var singleTenantOptions = new MultiTenancyOptions
        {
            Enabled = false, // Single-tenant mode
            DefaultTenantSlug = "default"
        };
        services.AddSingleton<IMultiTenancyOptions>(singleTenantOptions);
        services.AddSingleton<ITenantResolver, ModeAwareTenantResolver>();

        using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<AuthDbContext>();

        // Seed default tenant
        db.Tenants.Add(new Tenant
        {
            Id = _defaultTenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://localhost:5001",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var resolver = provider.GetRequiredService<ITenantResolver>();

        // Act - any path should resolve to default tenant
        var result1 = await resolver.ResolveTenantAsync("/authorize");
        var result2 = await resolver.ResolveTenantAsync("/t/acme/authorize");
        var result3 = await resolver.ResolveTenantAsync("/.well-known/openid-configuration");

        // Assert - all resolve to default tenant
        Assert.IsNotNull(result1);
        Assert.AreEqual(_defaultTenantId, result1.TenantId);
        Assert.AreEqual("default", result1.Slug);
        Assert.IsFalse(result1.IsMultiTenantMode);

        Assert.IsNotNull(result2);
        Assert.AreEqual(_defaultTenantId, result2.TenantId);
        Assert.IsFalse(result2.IsMultiTenantMode);

        Assert.IsNotNull(result3);
        Assert.AreEqual(_defaultTenantId, result3.TenantId);
    }

    [TestMethod]
    public async Task ResolveTenantAsync_SuspendedTenant_ReturnsNull()
    {
        // Arrange
        var suspendedTenantId = Guid.NewGuid();
        _db.Tenants.Add(new Tenant
        {
            Id = suspendedTenantId,
            Slug = "suspended",
            Name = "Suspended Tenant",
            IssuerUri = "https://localhost:5001/t/suspended",
            Status = TenantStatus.Suspended,
            SuspendedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act
        var result = await resolver.ResolveTenantAsync("/t/suspended/authorize");

        // Assert
        Assert.IsNull(result, "Suspended tenant should not be resolvable");
    }

    [TestMethod]
    public async Task ResolveTenantAsync_DeletedTenant_ReturnsNull()
    {
        // Arrange
        var deletedTenantId = Guid.NewGuid();
        _db.Tenants.Add(new Tenant
        {
            Id = deletedTenantId,
            Slug = "deleted",
            Name = "Deleted Tenant",
            IssuerUri = "https://localhost:5001/t/deleted",
            Status = TenantStatus.Deleted,
            DeletedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act
        var result = await resolver.ResolveTenantAsync("/t/deleted/authorize");

        // Assert
        Assert.IsNull(result, "Deleted tenant should not be resolvable");
    }

    [TestMethod]
    public async Task ResolveTenantAsync_Caching_ReturnsCachedResult()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - first call populates cache
        var result1 = await resolver.ResolveTenantAsync("/t/acme/authorize");

        // Delete tenant from DB to verify cache is used
        var tenant = await _db.Tenants.FindAsync(_acmeTenantId);
        _db.Tenants.Remove(tenant!);
        await _db.SaveChangesAsync();

        // Second call should return cached result
        var result2 = await resolver.ResolveTenantAsync("/t/acme/token");

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2, "Should return cached result even after tenant deleted from DB");
        Assert.AreEqual(result1.TenantId, result2.TenantId);
        Assert.AreEqual(result1.Slug, result2.Slug);
    }
}
