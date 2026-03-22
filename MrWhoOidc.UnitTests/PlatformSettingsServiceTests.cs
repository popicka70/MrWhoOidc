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

    private static IOptions<AuthOptions> CreateAuthOptions(bool enableTokenExchange = false)
        => Options.Create(new AuthOptions { EnableTokenExchange = enableTokenExchange });

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
        Assert.IsFalse(settings.EnableTokenExchange, "Default should inherit disabled AuthOptions");
        Assert.AreNotEqual(Guid.Empty, settings.Id);
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
        using var db = CreateDb();
        var cache = CreateCache();
        var service = new PlatformSettingsService(db, cache, CreateAuthOptions());

        var settings = await service.GetSettingsAsync();
        var originalUpdatedAt = settings.UpdatedAt;

        // Small delay to ensure timestamp difference
        await Task.Delay(10);

        // Act
        await service.UpdateSettingsAsync(settings, "test-admin");

        // Assert
        Assert.IsTrue(settings.UpdatedAt > originalUpdatedAt, "UpdatedAt should be updated");
    }
}
