using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Validation;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.MultiTenancy;

/// <summary>
/// Tests for multi-tenancy fixes implemented in the assessment.
/// Covers C1 (TenantResolver performance), C4 (duplicate name validation),
/// H2 (PendingVerification status), M1 (write guards), M4 (settings validation).
/// </summary>
[TestClass]
public class MultiTenancyFixesTests
{
    private ServiceProvider _serviceProvider = null!;
    private AuthDbContext _db = null!;
    private MockTenantAccessor _tenantAccessor = null!;
    private Guid _defaultTenantId;
    private Guid _acmeTenantId;
    private Guid _contosoTenantId;

    [TestInitialize]
    public async Task Setup()
    {
        var services = new ServiceCollection();

        // In-memory database for testing
        services.AddDbContext<AuthDbContext>(options =>
            options.UseInMemoryDatabase(databaseName: $"MultiTenancyFixesTestDb_{Guid.NewGuid()}"),
            ServiceLifetime.Singleton);

        // Memory cache
        services.AddMemoryCache();

        // Logging
        services.AddLogging();

        // Multi-tenancy state provider (multi-tenant mode enabled)
        var multiTenancyStateProvider = new MultiTenancyStateProvider("default", true);
        services.AddSingleton<IMultiTenancyStateProvider>(multiTenancyStateProvider);

        // Multi-tenancy options (for backward compatibility with resolver)
        services.AddSingleton<IMultiTenancyOptions>(multiTenancyStateProvider);

        // Tenant resolver
        services.AddSingleton<ITenantResolver, ModeAwareTenantResolver>();

        // Tenant service
        services.AddSingleton<ITenantService, TenantService>();

        // Tenant cache options
        services.Configure<TenantCacheOptions>(options =>
        {
            options.L1Expiration = TimeSpan.FromMinutes(15);
            options.L2Expiration = TimeSpan.FromHours(1);
        });

        // Register test hybrid cache
        services.AddSingleton<HybridCache, TestHybridCache>();

        // Register mock tenant accessor
        _tenantAccessor = new MockTenantAccessor();
        services.AddSingleton<ITenantAccessor>(_tenantAccessor);

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

    #region C1: TenantResolver Performance Fix

    [TestMethod]
    public async Task C1_TenantResolver_CaseInsensitiveSlug_UsesEfFunctionsLike()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act - Test case-insensitive resolution (the fix uses EF.Functions.Like)
        var result = await resolver.ResolveTenantAsync("/t/ACME/authorize");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(_acmeTenantId, result.TenantId);
        Assert.AreEqual("acme", result.Slug);
    }

    [TestMethod]
    public async Task C1_TenantResolver_MixedCaseSlug_ResolvesCorrectly()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantResolver>();

        // Act
        var result = await resolver.ResolveTenantAsync("/t/CoNtOsO/authorize");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(_contosoTenantId, result.TenantId);
        Assert.AreEqual("contoso", result.Slug);
    }

    #endregion

    #region C4: Tenant Name Uniqueness Validation

    [TestMethod]
    public async Task C4_TenantService_CreateTenantWithDuplicateName_ThrowsException()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            tenantService.CreateTenantAsync(
                "Acme Corporation", // Duplicate name
                Guid.NewGuid(),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task C4_TenantService_CreateTenantWithUniqueName_Succeeds()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();

        // Act
        var result = await tenantService.CreateTenantAsync(
            "Unique Tenant Name",
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("Unique Tenant Name", result.Name);
    }

    [TestMethod]
    public async Task C4_TenantService_CreateTenantWithCustomSlug_Succeeds()
    {
        // Arrange
        using var scope = _serviceProvider.CreateScope();
        var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();

        // Act
        var result = await tenantService.CreateTenantAsync(
            "Custom Slug Tenant",
            Guid.NewGuid(),
            "my-custom-slug",
            CancellationToken.None);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("my-custom-slug", result.Slug);
    }

    #endregion

    #region H2: Domain Claim Default Status

    [TestMethod]
    public async Task H2_TenantDomainClaimService_CreateClaim_DefaultStatusIsPendingVerification()
    {
        // Arrange
        using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var tenant = await SeedTenantAsync(db, "test", "Test");
        var service = new TenantDomainClaimService(
            db,
            NullLogger<TenantDomainClaimService>.Instance,
            Options.Create(new PublicEmailDomainOptions()));

        // Act
        var result = await service.CreateClaimAsync(
            tenant.Id,
            "example.com",
            TenantDomainEnrollmentMode.AutoJoin,
            null,
            null);

        // Assert
        Assert.AreEqual(TenantDomainClaimStatus.PendingVerification, result.Claim.Status);
    }

    [TestMethod]
    public async Task H2_TenantDomainClaimService_CreateClaim_WithPublicEmailDomain_ThrowsException()
    {
        // Arrange
        using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var tenant = await SeedTenantAsync(db, "test", "Test");
        var service = new TenantDomainClaimService(
            db,
            NullLogger<TenantDomainClaimService>.Instance,
            Options.Create(new PublicEmailDomainOptions()));

        // Act & Assert - Public email domains cannot be claimed
        await Assert.ThrowsExactlyAsync<ValidationException>(() =>
            service.CreateClaimAsync(
                tenant.Id,
                "gmail.com",
                TenantDomainEnrollmentMode.AutoJoin,
                null,
                null));
    }

    #endregion

    #region M1: Enhanced Tenant Write Guards

    [TestMethod]
    public async Task M1_TenantWriteGuards_RefusingToSaveDifferentTenant_ThrowsException()
    {
        // Arrange
        var mockAccessor = new MockTenantAccessor();
        var db = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            mockAccessor);

        var tenant1 = await SeedTenantAsync(db, "tenant1", "Tenant 1");
        var tenant2 = await SeedTenantAsync(db, "tenant2", "Tenant 2");

        // Set up tenant accessor with tenant1 context
        mockAccessor.SetTenant(new TenantContext
        {
            TenantId = tenant1.Id,
            Slug = "tenant1",
            Name = "Tenant 1",
            IssuerUri = $"https://localhost:5001/t/tenant1",
            IsMultiTenantMode = true
        });

        // Create a user with explicit TenantId set to a different tenant
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant2.Id, // Intentionally wrong tenant
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "TEST@EXAMPLE.COM",
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Users.Add(user);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task M1_TenantWriteGuards_CorrectTenantId_SavesSuccessfully()
    {
        // Arrange
        using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var tenant = await SeedTenantAsync(db, "test", "Test");

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id, // Correct tenant
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "TEST@EXAMPLE.COM",
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Users.Add(user);

        // Act
        await db.SaveChangesAsync();

        // Assert
        var savedUser = await db.Users.FirstOrDefaultAsync(u => u.Id == user.Id);
        Assert.IsNotNull(savedUser);
        Assert.AreEqual(tenant.Id, savedUser.TenantId);
    }

    [TestMethod]
    public async Task M1_TenantWriteGuards_NavigationToDifferentTenant_ThrowsException()
    {
        var mockAccessor = new MockTenantAccessor();
        var db = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            mockAccessor);

        var tenant1 = await SeedTenantAsync(db, "tenant1", "Tenant 1");
        var tenant2 = await SeedTenantAsync(db, "tenant2", "Tenant 2");

        mockAccessor.SetTenant(new TenantContext
        {
            TenantId = tenant1.Id,
            Slug = "tenant1",
            Name = "Tenant 1",
            IssuerUri = "https://localhost:5001/t/tenant1",
            IsMultiTenantMode = true
        });

        // A TenantIcon whose Tenant navigation points at the wrong tenant.
        var icon = new TenantIcon
        {
            Id = Guid.NewGuid(),
            Tenant = tenant2,
            FileName = "icon.png",
            ContentType = "image/png",
            FileData = Array.Empty<byte>(),
            FileSize = 0
        };
        db.Set<TenantIcon>().Add(icon);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    #endregion

    #region M4: Tenant Settings Validator

    [TestMethod]
    public void M4_TenantSettingsValidator_ValidJson_ReturnsTrue()
    {
        // Arrange
        var validator = new TenantSettingsValidator();
        var validJson = @"{""key"": ""value"", ""number"": 42}";

        // Act
        var result = validator.IsValid(validJson);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void M4_TenantSettingsValidator_InvalidJson_ReturnsFalse()
    {
        // Arrange
        var validator = new TenantSettingsValidator();
        var invalidJson = @"{invalid json}";

        // Act
        var result = validator.IsValid(invalidJson);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void M4_TenantSettingsValidator_EmptyString_ReturnsTrue()
    {
        // Arrange
        var validator = new TenantSettingsValidator();

        // Act
        var result = validator.IsValid(string.Empty);

        // Assert
        Assert.IsTrue(result);
    }

    #endregion

    #region Helper Methods

    private static async Task<Tenant> SeedTenantAsync(AuthDbContext db, string slug, string name)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = name,
            IssuerUri = $"https://localhost:5001/t/{slug}",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    #endregion
}
