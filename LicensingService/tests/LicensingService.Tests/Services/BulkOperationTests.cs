using System.Diagnostics;
using LicensingService.Core;
using LicensingService.Core.Crypto;
using LicensingService.Core.Entities;
using LicensingService.Core.Persistence;
using LicensingService.Core.Services;
using LicensingService.Core.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace LicensingService.Tests.Services;

[TestClass]
public class BulkOperationTests
{
    private LicensingDbContext _context = null!;
    private LicenseStore _licenseStore = null!;
    private ProductStore _productStore = null!;
    private CustomerStore _customerStore = null!;
    private LicenseService _licenseService = null!;
    private Mock<ISigningKeyService> _signingKeyServiceMock = null!;
    private LicenseTokenGenerator _tokenGenerator = null!;
    private Customer _testCustomer = null!;
    private LicensedProduct _testProduct = null!;

    [TestInitialize]
    public async Task Setup()
    {
        var options = new DbContextOptionsBuilder<LicensingDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new LicensingDbContext(options);

        // Setup signing key mock
        _signingKeyServiceMock = new Mock<ISigningKeyService>();
        var testKey = EcdsaKeyHelper.GenerateP256Key();
        var kid = Guid.NewGuid().ToString("N")[..16];
        _signingKeyServiceMock.Setup(x => x.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((testKey, kid));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Licensing:Issuer"] = "TestLicensingService"
            })
            .Build();

        _tokenGenerator = new LicenseTokenGenerator(
            _signingKeyServiceMock.Object,
            configuration);

        _licenseStore = new LicenseStore(_context, _tokenGenerator);
        _productStore = new ProductStore(_context);
        _customerStore = new CustomerStore(_context);

        _licenseService = new LicenseService(
            _licenseStore,
            _productStore,
            _customerStore,
            _tokenGenerator,
            Mock.Of<ILogger<LicenseService>>());

        // Create test customer and product
        _testCustomer = await _customerStore.CreateAsync(new Customer
        {
            Id = GuidHelper.NewId(),
            Identifier = "test-customer",
            DisplayName = "Test Customer",
            ContactEmail = "test@example.com",
            Status = "Active",
            CreatedAt = DateTimeOffset.UtcNow
        });

        _testProduct = await _productStore.CreateAsync(new LicensedProduct
        {
            Id = GuidHelper.NewId(),
            Identifier = "test-product",
            DisplayName = "Test Product",
            Status = "Active",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context?.Dispose();
    }

    #region Bulk Renew Tests

    [TestMethod]
    public async Task BulkRenew_RenewsMultipleLicenses()
    {
        // Arrange
        var licenses = await CreateMultipleLicenses(5);
        var newValidUntil = DateTimeOffset.UtcNow.AddYears(2);

        // Act
        var result = await _licenseService.BulkRenewLicensesAsync(new BulkRenewRequest
        {
            LicenseIds = licenses.Select(l => l.Id).ToList(),
            NewValidUntil = newValidUntil
        }, "admin-user");

        // Assert
        Assert.IsTrue(result.AllSucceeded);
        Assert.AreEqual(5, result.TotalRequested);
        Assert.AreEqual(5, result.SuccessCount);
        Assert.AreEqual(0, result.FailureCount);
    }

    [TestMethod]
    public async Task BulkRenew_CreatesNewLicensesForEach()
    {
        // Arrange
        var licenses = await CreateMultipleLicenses(3);
        var originalIds = licenses.Select(l => l.Id).ToList();
        var newValidUntil = DateTimeOffset.UtcNow.AddYears(2);

        // Act
        var result = await _licenseService.BulkRenewLicensesAsync(new BulkRenewRequest
        {
            LicenseIds = originalIds,
            NewValidUntil = newValidUntil
        }, "admin-user");

        // Assert
        Assert.AreEqual(3, result.Successes.Count);
        foreach (var success in result.Successes)
        {
            Assert.IsNotNull(success.NewLicenseId);
            Assert.IsNotNull(success.NewToken);
            Assert.AreNotEqual(success.OriginalLicenseId, success.NewLicenseId);
        }
    }

    [TestMethod]
    public async Task BulkRenew_SetsOriginalStatusToRenewed()
    {
        // Arrange
        var licenses = await CreateMultipleLicenses(3);
        var originalIds = licenses.Select(l => l.Id).ToList();

        // Act
        await _licenseService.BulkRenewLicensesAsync(new BulkRenewRequest
        {
            LicenseIds = originalIds,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(2)
        }, "admin-user");

        // Assert
        foreach (var originalId in originalIds)
        {
            var original = await _context.Licenses.FindAsync(originalId);
            Assert.AreEqual(LicenseStatus.Renewed, original!.Status);
        }
    }

    [TestMethod]
    public async Task BulkRenew_HandlesPartialFailure()
    {
        // Arrange
        var validLicenses = await CreateMultipleLicenses(3);
        var invalidId = Guid.NewGuid(); // Non-existent
        var allIds = validLicenses.Select(l => l.Id).Append(invalidId).ToList();

        // Act
        var result = await _licenseService.BulkRenewLicensesAsync(new BulkRenewRequest
        {
            LicenseIds = allIds,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(2)
        }, "admin-user");

        // Assert
        Assert.IsTrue(result.PartialSuccess);
        Assert.AreEqual(4, result.TotalRequested);
        Assert.AreEqual(3, result.SuccessCount);
        Assert.AreEqual(1, result.FailureCount);
        Assert.AreEqual(invalidId, result.Failures[0].LicenseId);
        Assert.AreEqual("license_not_found", result.Failures[0].ErrorCode);
    }

    [TestMethod]
    public async Task BulkRenew_SkipsRevokedLicenses()
    {
        // Arrange
        var license = (await CreateMultipleLicenses(1))[0];
        await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "Test revocation"
        }, "admin-user");

        // Act
        var result = await _licenseService.BulkRenewLicensesAsync(new BulkRenewRequest
        {
            LicenseIds = [license.Id],
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(2)
        }, "admin-user");

        // Assert
        Assert.AreEqual(0, result.SuccessCount);
        Assert.AreEqual(1, result.FailureCount);
        Assert.AreEqual("license_revoked", result.Failures[0].ErrorCode);
    }

    [TestMethod]
    public async Task BulkRenew_EmptyListReturnsSuccess()
    {
        // Act
        var result = await _licenseService.BulkRenewLicensesAsync(new BulkRenewRequest
        {
            LicenseIds = [],
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(2)
        }, "admin-user");

        // Assert
        Assert.IsTrue(result.AllSucceeded);
        Assert.AreEqual(0, result.TotalRequested);
    }

    #endregion

    #region Bulk Revoke Tests

    [TestMethod]
    public async Task BulkRevoke_RevokesMultipleLicenses()
    {
        // Arrange
        var licenses = await CreateMultipleLicenses(5);

        // Act
        var result = await _licenseService.BulkRevokeLicensesAsync(new BulkRevokeRequest
        {
            LicenseIds = licenses.Select(l => l.Id).ToList(),
            Reason = "Bulk revocation test"
        }, "admin-user");

        // Assert
        Assert.IsTrue(result.AllSucceeded);
        Assert.AreEqual(5, result.TotalRequested);
        Assert.AreEqual(5, result.SuccessCount);
        Assert.AreEqual(0, result.FailureCount);
    }

    [TestMethod]
    public async Task BulkRevoke_SetsCorrectStatusAndReason()
    {
        // Arrange
        var licenses = await CreateMultipleLicenses(3);
        var reason = "Contract terminated";

        // Act
        await _licenseService.BulkRevokeLicensesAsync(new BulkRevokeRequest
        {
            LicenseIds = licenses.Select(l => l.Id).ToList(),
            Reason = reason
        }, "admin-user");

        // Assert
        foreach (var license in licenses)
        {
            var revoked = await _context.Licenses.FindAsync(license.Id);
            Assert.AreEqual(LicenseStatus.Revoked, revoked!.Status);
            Assert.AreEqual(reason, revoked.RevocationReason);
            Assert.IsNotNull(revoked.RevokedAt);
        }
    }

    [TestMethod]
    public async Task BulkRevoke_HandlesPartialFailure()
    {
        // Arrange
        var validLicenses = await CreateMultipleLicenses(2);
        var invalidId = Guid.NewGuid();
        var allIds = validLicenses.Select(l => l.Id).Append(invalidId).ToList();

        // Act
        var result = await _licenseService.BulkRevokeLicensesAsync(new BulkRevokeRequest
        {
            LicenseIds = allIds,
            Reason = "Test"
        }, "admin-user");

        // Assert
        Assert.IsTrue(result.PartialSuccess);
        Assert.AreEqual(3, result.TotalRequested);
        Assert.AreEqual(2, result.SuccessCount);
        Assert.AreEqual(1, result.FailureCount);
    }

    [TestMethod]
    public async Task BulkRevoke_SkipsAlreadyRevokedLicenses()
    {
        // Arrange
        var license = (await CreateMultipleLicenses(1))[0];
        await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "First revocation"
        }, "admin-user");

        // Act
        var result = await _licenseService.BulkRevokeLicensesAsync(new BulkRevokeRequest
        {
            LicenseIds = [license.Id],
            Reason = "Second revocation"
        }, "admin-user");

        // Assert
        Assert.AreEqual(0, result.SuccessCount);
        Assert.AreEqual(1, result.FailureCount);
        Assert.AreEqual("already_revoked", result.Failures[0].ErrorCode);
    }

    [TestMethod]
    public async Task BulkRevoke_EmptyListReturnsSuccess()
    {
        // Act
        var result = await _licenseService.BulkRevokeLicensesAsync(new BulkRevokeRequest
        {
            LicenseIds = [],
            Reason = "Test"
        }, "admin-user");

        // Assert
        Assert.IsTrue(result.AllSucceeded);
        Assert.AreEqual(0, result.TotalRequested);
    }

    #endregion

    #region Performance Tests

    [TestMethod]
    public async Task BulkRenew_100Licenses_CompletesInUnder10Seconds()
    {
        // Arrange - Create 100 licenses
        var licenses = await CreateMultipleLicenses(100);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await _licenseService.BulkRenewLicensesAsync(new BulkRenewRequest
        {
            LicenseIds = licenses.Select(l => l.Id).ToList(),
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(2)
        }, "admin-user");
        stopwatch.Stop();

        // Assert
        Assert.IsTrue(result.AllSucceeded, $"Expected all to succeed, but {result.FailureCount} failed");
        Assert.AreEqual(100, result.SuccessCount);
        Assert.IsTrue(stopwatch.Elapsed.TotalSeconds < 10, 
            $"Expected completion in under 10 seconds, took {stopwatch.Elapsed.TotalSeconds:F2} seconds");
    }

    [TestMethod]
    public async Task BulkRevoke_100Licenses_CompletesInUnder10Seconds()
    {
        // Arrange - Create 100 licenses
        var licenses = await CreateMultipleLicenses(100);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await _licenseService.BulkRevokeLicensesAsync(new BulkRevokeRequest
        {
            LicenseIds = licenses.Select(l => l.Id).ToList(),
            Reason = "Bulk performance test"
        }, "admin-user");
        stopwatch.Stop();

        // Assert
        Assert.IsTrue(result.AllSucceeded, $"Expected all to succeed, but {result.FailureCount} failed");
        Assert.AreEqual(100, result.SuccessCount);
        Assert.IsTrue(stopwatch.Elapsed.TotalSeconds < 10, 
            $"Expected completion in under 10 seconds, took {stopwatch.Elapsed.TotalSeconds:F2} seconds");
    }

    #endregion

    #region Helper Methods

    private async Task<List<License>> CreateMultipleLicenses(int count)
    {
        var licenses = new List<License>();
        
        for (int i = 0; i < count; i++)
        {
            var result = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
            {
                CustomerId = _testCustomer.Id,
                ProductId = _testProduct.Id,
                Tier = "Standard",
                ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
            }, "test-user");

            Assert.IsTrue(result.Success, $"Failed to create license {i + 1}: {result.Error}");
            licenses.Add(result.License!);
        }

        return licenses;
    }

    #endregion
}
