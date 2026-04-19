using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Seeding;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Seeding;

[TestClass]
public class SeedManifestApplierPerformanceTests
{
    private AuthDbContext _db = null!;
    private Mock<ITenantAccessor> _tenantAccessor = null!;
    private Mock<IMultiTenancyOptions> _multiTenancyOptions = null!;
    private Mock<IIssuerBuilder> _issuerBuilder = null!;
    private Mock<IOptions<OidcOptions>> _oidcOptions = null!;
    private Mock<IOptions<SeedManifestOptions>> _seedOptions = null!;
    private Mock<IConfiguration> _configuration = null!;
    private Mock<IPasswordHasher> _passwordHasher = null!;
    private Mock<IClientStore> _clientStore = null!;
    private Mock<IPlatformSettingsService> _platformSettingsService = null!;
    private Mock<ILicenseService> _licenseService = null!;
    private Mock<IUserAccountProvisioner> _accountProvisioner = null!;
    private Mock<ILogger<SeedManifestApplier>> _logger = null!;
    private SeedManifestApplier _applier = null!;
    private string _tempFilePath = null!;

    [TestInitialize]
    public void Setup()
    {
        _db = TestDataSeeder.CreateInMemoryDb();
        _tenantAccessor = new Mock<ITenantAccessor>();
        _multiTenancyOptions = new Mock<IMultiTenancyOptions>();
        _issuerBuilder = new Mock<IIssuerBuilder>();
        _oidcOptions = new Mock<IOptions<OidcOptions>>();
        _seedOptions = new Mock<IOptions<SeedManifestOptions>>();
        _configuration = new Mock<IConfiguration>();
        _passwordHasher = new Mock<IPasswordHasher>();
        _clientStore = new Mock<IClientStore>();
        _platformSettingsService = new Mock<IPlatformSettingsService>();
        _licenseService = new Mock<ILicenseService>();
        _accountProvisioner = new Mock<IUserAccountProvisioner>();
        _logger = new Mock<ILogger<SeedManifestApplier>>();

        _oidcOptions.Setup(o => o.Value).Returns(new OidcOptions());
        _seedOptions.Setup(o => o.Value).Returns(new SeedManifestOptions());
        _platformSettingsService.Setup(service => service.GetSettingsAsync()).ReturnsAsync(new PlatformSettings());
        _platformSettingsService
            .Setup(service => service.UpdateSettingsAsync(It.IsAny<PlatformSettings>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        _licenseService.Setup(l => l.InstallLicenseAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LicenseValidationResult(true, null, null, null));
        _accountProvisioner
            .Setup(service => service.EnsureAsync(It.IsAny<User>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        _applier = new SeedManifestApplier(
            _db,
            _tenantAccessor.Object,
            _multiTenancyOptions.Object,
            _issuerBuilder.Object,
            _oidcOptions.Object,
            _seedOptions.Object,
            _configuration.Object,
            _passwordHasher.Object,
            _clientStore.Object,
            _platformSettingsService.Object,
            _licenseService.Object,
            _accountProvisioner.Object,
            _logger.Object
        );

        _tempFilePath = Path.GetTempFileName();
        File.WriteAllText(_tempFilePath, "test-license-token");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(_tempFilePath))
        {
            File.Delete(_tempFilePath);
        }
        _db.Dispose();
    }

    [TestMethod]
    public async Task ApplyLicensesAsync_WithFileToken_Benchmark()
    {
        var manifest = new SeedManifest
        {
            Licenses = new List<LicenseSeedDefinition>
            {
                new LicenseSeedDefinition
                {
                    LicenseTokenPath = _tempFilePath
                }
            }
        };

        // Run once to warm up
        await _applier.ApplyLicensesAsync(manifest);

        var iterations = 1000;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            await _applier.ApplyLicensesAsync(manifest);
        }

        sw.Stop();
        Console.WriteLine($"Elapsed time for {iterations} iterations: {sw.ElapsedMilliseconds}ms");

        // Ensure it works
        _licenseService.Verify(l => l.InstallLicenseAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }
}
