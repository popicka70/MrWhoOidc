using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Seeding;

[TestClass]
public class TenantSeedingServicePerformanceTests
{
    private AuthDbContext _db = null!;
    private Mock<IPasswordHasher> _passwordHasher = null!;
    private Mock<ITenantService> _tenantService = null!;
    private Mock<ILogger<TenantSeedingService>> _logger = null!;
    private Mock<IUserAccountProvisioner> _accountProvisioner = null!;
    private Mock<IOptions<OidcOptions>> _oidcOptions = null!;
    private Mock<IIssuerBuilder> _issuerBuilder = null!;
    private Mock<IHttpContextAccessor> _httpContextAccessor = null!;
    private TenantSeedingService _seedingService = null!;

    [TestInitialize]
    public void Setup()
    {
        _db = TestDataSeeder.CreateInMemoryDb();
        _passwordHasher = new Mock<IPasswordHasher>();
        _tenantService = new Mock<ITenantService>();
        _logger = new Mock<ILogger<TenantSeedingService>>();
        _accountProvisioner = new Mock<IUserAccountProvisioner>();
        _oidcOptions = new Mock<IOptions<OidcOptions>>();
        _issuerBuilder = new Mock<IIssuerBuilder>();
        _httpContextAccessor = new Mock<IHttpContextAccessor>();

        _oidcOptions.Setup(o => o.Value).Returns(new OidcOptions { SampleWebClientBaseUrl = "http://localhost:5000" });
        _issuerBuilder.Setup(i => i.BuildIssuer(It.IsAny<string>(), It.IsAny<string>())).Returns("https://localhost/t/slug");
        _tenantService.Setup(t => t.CanProvisionTenantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        _seedingService = new TenantSeedingService(
            _db,
            _passwordHasher.Object,
            _tenantService.Object,
            _logger.Object,
            _accountProvisioner.Object,
            _oidcOptions.Object,
            _issuerBuilder.Object,
            _httpContextAccessor.Object
        );
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    [TestMethod]
    public async Task SeedSampleTenantAsync_PerformanceTest()
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            var slug = $"test-tenant-{i}";
            var result = await _seedingService.SeedSampleTenantAsync(slug, "Test Tenant");
            Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        }
        sw.Stop();

        Console.WriteLine($"TIME_TAKEN_MS: {sw.ElapsedMilliseconds}");
    }
}
