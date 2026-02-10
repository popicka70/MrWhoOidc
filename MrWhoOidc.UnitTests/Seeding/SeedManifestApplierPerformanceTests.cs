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
    private AuthDbContext _db;
    private Mock<ITenantAccessor> _tenantAccessor;
    private Mock<IMultiTenancyOptions> _multiTenancyOptions;
    private Mock<IIssuerBuilder> _issuerBuilder;
    private Mock<IOptions<OidcOptions>> _oidcOptions;
    private Mock<IOptions<SeedManifestOptions>> _seedOptions;
    private Mock<IConfiguration> _configuration;
    private Mock<IPasswordHasher> _passwordHasher;
    private Mock<IClientStore> _clientStore;
    private Mock<ILicenseService> _licenseService;
    private Mock<ILogger<SeedManifestApplier>> _logger;
    private SeedManifestApplier _applier;
    private string _tempFilePath;

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
        _licenseService = new Mock<ILicenseService>();
        _logger = new Mock<ILogger<SeedManifestApplier>>();

        _oidcOptions.Setup(o => o.Value).Returns(new OidcOptions());
        _seedOptions.Setup(o => o.Value).Returns(new SeedManifestOptions());

        _licenseService.Setup(l => l.InstallLicenseAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LicenseValidationResult(true, null, null, null));

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
            _licenseService.Object,
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
