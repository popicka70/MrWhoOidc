using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class PlatformSettingsServiceTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static HybridCache CreateCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache(); // L1-only mode for tests
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<HybridCache>();
    }

    private static IOptions<AuthOptions> CreateAuthOptions(bool enableTokenExchange = false, bool enableDynamicClientRegistration = false)
        => Options.Create(new AuthOptions
        {
            EnableTokenExchange = enableTokenExchange,
            EnableDynamicClientRegistration = enableDynamicClientRegistration
        });

    [TestMethod]
    public async Task GetSettingsAsync_CreatesDefault_WhenNotExists()
    {
        // Arrange
        using var db = CreateDb();
        var cache = CreateCache();
        var service = new PlatformSettingsService(db, cache, CreateAuthOptions());

        // Act
        var settings = await service.GetSettingsAsync();

        // Assert
        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.QrLoginAtDiscoveryEnabled, "Default should be disabled");
        Assert.IsFalse(settings.DynamicClientRegistrationEnabled, "Default should inherit disabled AuthOptions");
        Assert.IsFalse(settings.EnableTokenExchange, "Default should inherit disabled AuthOptions");
        Assert.AreNotEqual(Guid.Empty, settings.Id);
    }

    [TestMethod]
    public async Task GetSettingsAsync_CreatesDefault_WithDynamicClientRegistrationInheritedFromAuthOptions()
    {
        using var db = CreateDb();
        var cache = CreateCache();
        var service = new PlatformSettingsService(db, cache, CreateAuthOptions(enableDynamicClientRegistration: true));

        var settings = await service.GetSettingsAsync();

        Assert.IsTrue(settings.DynamicClientRegistrationEnabled);
    }

    [TestMethod]
    public async Task GetSettingsAsync_CreatesDefault_WithTokenExchangeInheritedFromAuthOptions()
    {
        using var db = CreateDb();
        var cache = CreateCache();
        var service = new PlatformSettingsService(db, cache, CreateAuthOptions(enableTokenExchange: true));

        var settings = await service.GetSettingsAsync();

        Assert.IsTrue(settings.EnableTokenExchange);
    }

    [TestMethod]
    public async Task GetSettingsAsync_ReturnsExisting_WhenExists()
    {
        // Arrange
        using var db = CreateDb();
        var cache = CreateCache();

        // Seed existing settings
        var existingSettings = new PlatformSettings
        {
            Id = GuidHelper.NewId(),
            QrLoginAtDiscoveryEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.PlatformSettings.Add(existingSettings);
        await db.SaveChangesAsync();

        var service = new PlatformSettingsService(db, cache, CreateAuthOptions());

        // Act
        var settings = await service.GetSettingsAsync();

        // Assert
        Assert.IsNotNull(settings);
        Assert.IsTrue(settings.QrLoginAtDiscoveryEnabled);
        Assert.AreEqual(existingSettings.Id, settings.Id);
    }

    [TestMethod]
    public async Task UpdateSettingsAsync_PersistsChanges()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        using var db = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options);
        var cache = CreateCache();
        var service = new PlatformSettingsService(db, cache, CreateAuthOptions());

        var settings = await service.GetSettingsAsync();
        Assert.IsFalse(settings.QrLoginAtDiscoveryEnabled);

        // Act
        settings.QrLoginAtDiscoveryEnabled = true;
        settings.EnableTokenExchange = true;
        await service.UpdateSettingsAsync(settings, "test-admin");

        // Verify in fresh context (bypassing cache) - use same InMemory database name
        using var verifyDb = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options);

        var verifySettings = await verifyDb.PlatformSettings.FirstOrDefaultAsync();

        // Assert
        Assert.IsNotNull(verifySettings);
        Assert.IsTrue(verifySettings.QrLoginAtDiscoveryEnabled);
        Assert.IsTrue(verifySettings.EnableTokenExchange);
        Assert.AreEqual("test-admin", verifySettings.UpdatedBy);
    }

    [TestMethod]
    public async Task IsQrLoginAtDiscoveryEnabledAsync_ReturnsFalse_WhenDisabled()
    {
        // Arrange
        using var db = CreateDb();
        var cache = CreateCache();
        var service = new PlatformSettingsService(db, cache, CreateAuthOptions());

        // Act
        var enabled = await service.IsQrLoginAtDiscoveryEnabledAsync();

        // Assert
        Assert.IsFalse(enabled);
    }

    [TestMethod]
    public async Task IsQrLoginAtDiscoveryEnabledAsync_ReturnsTrue_WhenEnabled()
    {
        // Arrange
        using var db = CreateDb();
        var cache = CreateCache();

        // Seed settings with QR enabled
        var settings = new PlatformSettings
        {
            Id = GuidHelper.NewId(),
            QrLoginAtDiscoveryEnabled = true
        };
        db.PlatformSettings.Add(settings);
        await db.SaveChangesAsync();

        var service = new PlatformSettingsService(db, cache, CreateAuthOptions());

        // Act
        var enabled = await service.IsQrLoginAtDiscoveryEnabledAsync();

        // Assert
        Assert.IsTrue(enabled);
    }

    [TestMethod]
    public async Task UpdateSettingsAsync_SetsUpdatedAtTimestamp()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        using var db = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options);
        var cache = CreateCache();
        var service = new PlatformSettingsService(db, cache, CreateAuthOptions());

        var settings = await service.GetSettingsAsync();
        var originalUpdatedAt = settings.UpdatedAt;

        // Small delay to ensure timestamp difference
        await Task.Delay(10);

        // Act
        await service.UpdateSettingsAsync(settings, "test-admin");

        using var verifyDb = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options);
        var persisted = await verifyDb.PlatformSettings.FirstAsync();

        // Assert
        Assert.IsTrue(persisted.UpdatedAt > originalUpdatedAt, "UpdatedAt should be updated");
    }

    [TestMethod]
    public async Task GetSettingsAsync_ReturnsIndependentSnapshots()
    {
        using var db = CreateDb();
        var cache = CreateCache();
        var service = new PlatformSettingsService(db, cache, CreateAuthOptions());

        var firstRead = await service.GetSettingsAsync();
        firstRead.DynamicClientRegistrationEnabled = true;
        firstRead.QrLoginAtDiscoveryEnabled = true;

        var secondRead = await service.GetSettingsAsync();

        Assert.IsFalse(secondRead.DynamicClientRegistrationEnabled, "Mutating a returned settings snapshot should not mutate the cached value.");
        Assert.IsFalse(secondRead.QrLoginAtDiscoveryEnabled, "Mutating a returned settings snapshot should not leak into later reads.");
    }

    [TestMethod]
    public async Task UpdateSettingsAsync_UpsertsCurrentStore_WhenCacheContainsSnapshotFromDifferentStore()
    {
        var cache = CreateCache();
        var authOptions = CreateAuthOptions();

        using (var firstDb = new AuthDbContext(
                   new DbContextOptionsBuilder<AuthDbContext>()
                       .UseInMemoryDatabase("platform-settings-store-a-" + Guid.NewGuid().ToString("N"))
                       .Options))
        {
            var firstService = new PlatformSettingsService(firstDb, cache, authOptions);
            _ = await firstService.GetSettingsAsync();
        }

        var secondDbName = "platform-settings-store-b-" + Guid.NewGuid().ToString("N");
        using (var secondDb = new AuthDbContext(
                   new DbContextOptionsBuilder<AuthDbContext>()
                       .UseInMemoryDatabase(secondDbName)
                       .Options))
        {
            var secondService = new PlatformSettingsService(secondDb, cache, authOptions);
            var staleCachedSnapshot = await secondService.GetSettingsAsync();

            staleCachedSnapshot.DynamicClientRegistrationEnabled = true;
            staleCachedSnapshot.EnableTokenExchange = true;

            await secondService.UpdateSettingsAsync(staleCachedSnapshot, "test-admin");
        }

        using var verifyDb = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>()
                .UseInMemoryDatabase(secondDbName)
                .Options);

        var persisted = await verifyDb.PlatformSettings.FirstOrDefaultAsync();

        Assert.IsNotNull(persisted, "Update should create the platform settings row when the current store does not have one.");
        Assert.IsTrue(persisted.DynamicClientRegistrationEnabled);
        Assert.IsTrue(persisted.EnableTokenExchange);
        Assert.AreEqual("test-admin", persisted.UpdatedBy);
    }
}
