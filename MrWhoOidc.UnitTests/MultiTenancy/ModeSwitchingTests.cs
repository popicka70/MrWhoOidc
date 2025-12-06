using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.MultiTenancy;

/// <summary>
/// Tests for mode switching behavior between single-tenant and multi-tenant modes.
/// Verifies correct tenant resolution, issuer construction, and context behavior
/// based on MultiTenancyOptions.Enabled setting.
/// </summary>
[TestClass]
public class ModeSwitchingTests
{
    private ServiceProvider _serviceProvider = null!;
    private AuthDbContext _db = null!;
    private Guid _defaultTenantId;
    private Guid _tenant1Id;
    private Guid _tenant2Id;
    private string _databaseName = null!;

    [TestCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        _serviceProvider?.Dispose();
    }

    private async Task<ServiceProvider> CreateServiceProvider(bool multiTenancyEnabled)
    {
        var services = new ServiceCollection();

        // Use a consistent database name so all contexts share the same in-memory database
        _databaseName = $"ModeSwitchingTests_{Guid.NewGuid()}";

        // Configure in-memory database
        services.AddDbContext<AuthDbContext>(options =>
            options.UseInMemoryDatabase(_databaseName));

        // Configure multi-tenancy options
        var multiTenancyOptions = new MultiTenancyOptions
        {
            Enabled = multiTenancyEnabled,
            DefaultTenantSlug = "default"
        };
        services.AddSingleton<IMultiTenancyOptions>(multiTenancyOptions);

        // Add required services
        services.AddMemoryCache();
        services.AddLogging();
        services.AddScoped<ITenantResolver, ModeAwareTenantResolver>();
        services.AddScoped<ITenantAccessor, TenantAccessor>();
        services.AddScoped<ITenantService, TenantService>();
        services.AddSingleton<HybridCache, TestHybridCache>();
        services.AddScoped<IIssuerBuilder, IssuerBuilder>();

        var provider = services.BuildServiceProvider();
        _db = provider.GetRequiredService<AuthDbContext>();

        // Seed test data
        await SeedTestDataAsync();

        return provider;
    }

    private async Task SeedTestDataAsync()
    {
        // Create default tenant (for single-tenant mode)
        var defaultTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Default Tenant",
            Slug = "default",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _defaultTenantId = defaultTenant.Id;

        // Create two additional tenants (for multi-tenant mode)
        var tenant1 = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme Corporation",
            Slug = "acme",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _tenant1Id = tenant1.Id;

        var tenant2 = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Contoso Ltd",
            Slug = "contoso",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _tenant2Id = tenant2.Id;

        _db.Tenants.AddRange(defaultTenant, tenant1, tenant2);
        await _db.SaveChangesAsync();
    }

    #region Mode Detection Tests

    [TestMethod]
    public async Task ModeDetection_MultiTenancyEnabled_MultiTenantModeActive()
    {
        // Arrange
        _serviceProvider = await CreateServiceProvider(multiTenancyEnabled: true);
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - resolve with tenant path
        var result = await resolver.ResolveTenantAsync("/t/acme/authorize");

        // Assert
        Assert.IsNotNull(result, "Should resolve tenant in multi-tenant mode");
        Assert.IsTrue(result.IsMultiTenantMode, "IsMultiTenantMode should be true");
        Assert.AreEqual("acme", result.Slug);
    }

    [TestMethod]
    public async Task ModeDetection_MultiTenancyDisabled_SingleTenantModeActive()
    {
        // Arrange
        _serviceProvider = await CreateServiceProvider(multiTenancyEnabled: false);
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - any path should resolve to default tenant
        var result1 = await resolver.ResolveTenantAsync("/authorize");
        var result2 = await resolver.ResolveTenantAsync("/t/acme/authorize"); // Ignored in single-tenant mode
        var result3 = await resolver.ResolveTenantAsync("/.well-known/openid-configuration");

        // Assert - all resolve to default tenant
        Assert.IsNotNull(result1);
        Assert.IsFalse(result1.IsMultiTenantMode, "IsMultiTenantMode should be false");
        Assert.AreEqual("default", result1.Slug);

        Assert.IsNotNull(result2);
        Assert.IsFalse(result2.IsMultiTenantMode);
        Assert.AreEqual("default", result2.Slug, "Should ignore tenant path in single-tenant mode");

        Assert.IsNotNull(result3);
        Assert.IsFalse(result3.IsMultiTenantMode);
        Assert.AreEqual("default", result3.Slug);
    }

    #endregion

    #region Issuer Resolution Tests

    [TestMethod]
    public async Task IssuerResolution_MultiTenantMode_PathBasedIssuer()
    {
        // Arrange
        _serviceProvider = await CreateServiceProvider(multiTenancyEnabled: true);
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();
        var issuerBuilder = scope.ServiceProvider.GetRequiredService<IIssuerBuilder>();
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();

        // Act - resolve Acme tenant
        var tenant = await resolver.ResolveTenantAsync("/t/acme/authorize");
        Assert.IsNotNull(tenant);

        // Manually set tenant context (simulating middleware)
        ((TenantAccessor)tenantAccessor).SetTenant(tenant);

        var issuer = issuerBuilder.BuildIssuer("https://op.example.com");

        // Assert
        Assert.AreEqual("https://op.example.com/t/acme", issuer, "Multi-tenant mode should use path-based issuer");
    }

    [TestMethod]
    public async Task IssuerResolution_SingleTenantMode_RootIssuer()
    {
        // Arrange
        _serviceProvider = await CreateServiceProvider(multiTenancyEnabled: false);
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();
        var issuerBuilder = scope.ServiceProvider.GetRequiredService<IIssuerBuilder>();
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();

        // Act - resolve default tenant
        var tenant = await resolver.ResolveTenantAsync("/authorize");
        Assert.IsNotNull(tenant);

        // Manually set tenant context
        ((TenantAccessor)tenantAccessor).SetTenant(tenant);

        var issuer = issuerBuilder.BuildIssuer("https://op.example.com");

        // Assert
        Assert.AreEqual("https://op.example.com", issuer, "Single-tenant mode should use root issuer");
    }

    [TestMethod]
    public async Task IssuerResolution_MultiTenantMode_DifferentTenantsHaveDifferentIssuers()
    {
        // Arrange
        _serviceProvider = await CreateServiceProvider(multiTenancyEnabled: true);
        using var scope = _serviceProvider.CreateScope();
        var issuerBuilder = scope.ServiceProvider.GetRequiredService<IIssuerBuilder>();

        // Act - build issuers for different tenants
        var issuerAcme = issuerBuilder.BuildIssuer("https://op.example.com", "acme");
        var issuerContoso = issuerBuilder.BuildIssuer("https://op.example.com", "contoso");

        // Assert
        Assert.AreEqual("https://op.example.com/t/acme", issuerAcme);
        Assert.AreEqual("https://op.example.com/t/contoso", issuerContoso);
        Assert.AreNotEqual(issuerAcme, issuerContoso, "Different tenants should have different issuers");
    }

    #endregion

    #region Discovery Endpoint Behavior Tests

    [TestMethod]
    public async Task DiscoveryEndpoint_MultiTenantMode_TenantSpecificPath()
    {
        // Arrange
        _serviceProvider = await CreateServiceProvider(multiTenancyEnabled: true);
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - resolve from discovery path
        var tenant = await resolver.ResolveTenantAsync("/t/acme/.well-known/openid-configuration");

        // Assert
        Assert.IsNotNull(tenant);
        Assert.AreEqual("acme", tenant.Slug);
        Assert.IsTrue(tenant.IsMultiTenantMode);
    }

    [TestMethod]
    public async Task DiscoveryEndpoint_SingleTenantMode_RootPath()
    {
        // Arrange
        _serviceProvider = await CreateServiceProvider(multiTenancyEnabled: false);
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - resolve from root discovery path
        var tenant = await resolver.ResolveTenantAsync("/.well-known/openid-configuration");

        // Assert
        Assert.IsNotNull(tenant);
        Assert.AreEqual("default", tenant.Slug);
        Assert.IsFalse(tenant.IsMultiTenantMode);
    }

    #endregion

    #region Tenant Context Tests

    [TestMethod]
    public async Task TenantContext_MultiTenantMode_ResolvedFromPath()
    {
        // Arrange
        _serviceProvider = await CreateServiceProvider(multiTenancyEnabled: true);
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - resolve different tenant paths
        var acmeTenant = await resolver.ResolveTenantAsync("/t/acme/authorize");
        var contosoTenant = await resolver.ResolveTenantAsync("/t/contoso/token");

        // Assert
        Assert.IsNotNull(acmeTenant);
        Assert.AreEqual(_tenant1Id, acmeTenant.TenantId);
        Assert.AreEqual("acme", acmeTenant.Slug);

        Assert.IsNotNull(contosoTenant);
        Assert.AreEqual(_tenant2Id, contosoTenant.TenantId);
        Assert.AreEqual("contoso", contosoTenant.Slug);

        Assert.AreNotEqual(acmeTenant.TenantId, contosoTenant.TenantId);
    }

    [TestMethod]
    public async Task TenantContext_SingleTenantMode_AlwaysDefaultTenant()
    {
        // Arrange
        _serviceProvider = await CreateServiceProvider(multiTenancyEnabled: false);
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - try various paths
        var result1 = await resolver.ResolveTenantAsync("/authorize");
        var result2 = await resolver.ResolveTenantAsync("/t/acme/authorize");
        var result3 = await resolver.ResolveTenantAsync("/t/contoso/token");
        var result4 = await resolver.ResolveTenantAsync("/.well-known/openid-configuration");

        // Assert - all should resolve to default tenant
        Assert.IsNotNull(result1);
        Assert.AreEqual(_defaultTenantId, result1.TenantId);

        Assert.IsNotNull(result2);
        Assert.AreEqual(_defaultTenantId, result2.TenantId);

        Assert.IsNotNull(result3);
        Assert.AreEqual(_defaultTenantId, result3.TenantId);

        Assert.IsNotNull(result4);
        Assert.AreEqual(_defaultTenantId, result4.TenantId);

        // All should be the same tenant
        Assert.AreEqual(result1.TenantId, result2.TenantId);
        Assert.AreEqual(result2.TenantId, result3.TenantId);
        Assert.AreEqual(result3.TenantId, result4.TenantId);
    }

    #endregion

    #region Fallback Behavior Tests

    [TestMethod]
    public async Task Fallback_MultiTenantMode_NoTenantPath_ReturnsDefaultTenant()
    {
        // Arrange
        _serviceProvider = await CreateServiceProvider(multiTenancyEnabled: true);
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - path without /t/{slug} prefix
        var result = await resolver.ResolveTenantAsync("/.well-known/openid-configuration");

        // Assert - should fall back to default tenant for backward compatibility
        Assert.IsNotNull(result);
        Assert.AreEqual("default", result.Slug);
        Assert.AreEqual(_defaultTenantId, result.TenantId);
    }

    [TestMethod]
    public async Task Fallback_MultiTenantMode_InvalidTenantSlug_ReturnsNull()
    {
        // Arrange
        _serviceProvider = await CreateServiceProvider(multiTenancyEnabled: true);
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - path with non-existent tenant
        var result = await resolver.ResolveTenantAsync("/t/nonexistent/authorize");

        // Assert - should return null (404)
        Assert.IsNull(result, "Non-existent tenant should return null");
    }

    #endregion
}
