using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
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
public class SeedManifestApplierTests
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
    private Mock<ILicenseService> _licenseService = null!;
    private Mock<ILogger<SeedManifestApplier>> _logger = null!;
    private SeedManifestApplier _applier = null!;

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
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    [TestMethod]
    public async Task ApplyLicensesAsync_WhenLicenseServiceThrows_ShouldCatchExceptionAndLogWarning()
    {
        // Arrange
        var manifest = new SeedManifest
        {
            Licenses = new List<LicenseSeedDefinition>
            {
                new LicenseSeedDefinition
                {
                    LicenseToken = "dummy-token"
                }
            }
        };

        var expectedException = new InvalidOperationException("Test exception");

        _licenseService.Setup(l => l.InstallLicenseAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act
        // This should not throw, it should catch the exception internally and log it.
        await _applier.ApplyLicensesAsync(manifest);

        // Assert
#pragma warning disable CS8602, CS8620
        _logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Seed manifest license installation failed.")),
                expectedException,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
#pragma warning restore CS8602, CS8620
    }
}
