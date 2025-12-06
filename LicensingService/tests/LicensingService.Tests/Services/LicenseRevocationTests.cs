using System.Text.Json;
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
public class LicenseRevocationTests
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

        // LicenseStore requires token generator
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

    #region Status Transition Tests

    [TestMethod]
    public async Task RevokeLicense_SetsStatusToRevoked()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        Assert.IsTrue(issueResult.Success, $"Issue failed: {issueResult.Error}");
        var license = issueResult.License!;

        // Act
        var revokeResult = await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "Customer requested cancellation"
        }, "admin-user");

        // Assert
        Assert.IsTrue(revokeResult.Success, $"Revoke failed: {revokeResult.Error}");
        Assert.AreEqual(LicenseStatus.Revoked, revokeResult.License!.Status);
    }

    [TestMethod]
    public async Task RevokeLicense_SetsRevokedAt()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var license = issueResult.License!;
        var beforeRevoke = DateTimeOffset.UtcNow;

        // Act
        var revokeResult = await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "Fraud detected"
        }, "admin-user");

        // Assert
        Assert.IsNotNull(revokeResult.License!.RevokedAt);
        Assert.IsTrue(revokeResult.License.RevokedAt >= beforeRevoke);
        Assert.IsTrue(revokeResult.License.RevokedAt <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [TestMethod]
    public async Task RevokeLicense_RecordsRevokedBy()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var license = issueResult.License!;

        // Act
        var revokeResult = await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "Policy violation"
        }, "security-admin");

        // Assert
        Assert.AreEqual("security-admin", revokeResult.License!.RevokedBy);
    }

    [TestMethod]
    public async Task RevokeLicense_RecordsRevocationReason()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var license = issueResult.License!;
        var reason = "Customer violated terms of service - unauthorized redistribution";

        // Act
        var revokeResult = await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = reason
        }, "admin-user");

        // Assert
        Assert.AreEqual(reason, revokeResult.License!.RevocationReason);
    }

    #endregion

    #region Validation Tests

    [TestMethod]
    public async Task RevokeLicense_CannotRevokeTwice()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var license = issueResult.License!;

        // First revocation
        await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "First revocation"
        }, "admin-user");

        // Act - attempt second revocation
        var secondRevokeResult = await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "Second revocation attempt"
        }, "admin-user");

        // Assert
        Assert.IsFalse(secondRevokeResult.Success);
        Assert.AreEqual("already_revoked", secondRevokeResult.ErrorCode);
    }

    [TestMethod]
    public async Task RevokeLicense_RequiresReason()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var license = issueResult.License!;

        // Act - attempt revocation without reason
        var revokeResult = await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = ""  // Empty reason
        }, "admin-user");

        // Assert
        Assert.IsFalse(revokeResult.Success);
        Assert.AreEqual("reason_required", revokeResult.ErrorCode);
    }

    [TestMethod]
    public async Task RevokeLicense_RequiresNonWhitespaceReason()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var license = issueResult.License!;

        // Act - attempt revocation with whitespace-only reason
        var revokeResult = await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "   "  // Whitespace only
        }, "admin-user");

        // Assert
        Assert.IsFalse(revokeResult.Success);
        Assert.AreEqual("reason_required", revokeResult.ErrorCode);
    }

    [TestMethod]
    public async Task RevokeLicense_FailsForNonexistentLicense()
    {
        // Act
        var revokeResult = await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = Guid.NewGuid(),
            Reason = "Test reason"
        }, "admin-user");

        // Assert
        Assert.IsFalse(revokeResult.Success);
        Assert.AreEqual("license_not_found", revokeResult.ErrorCode);
    }

    #endregion

    #region Audit Event Tests

    [TestMethod]
    public async Task RevokeLicense_CreatesAuditEvent()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var license = issueResult.License!;
        var reason = "Customer requested cancellation";

        // Act
        await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = reason
        }, "admin-user");

        // Assert
        var events = await _context.LicenseEvents
            .Where(e => e.LicenseId == license.Id && e.EventType == LicenseEventType.Revoked)
            .ToListAsync();

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("admin-user", events[0].Actor);
        Assert.AreEqual(reason, events[0].Details);
    }

    #endregion

    #region Side Effect Tests

    [TestMethod]
    public async Task RevokeLicense_CannotRenewRevokedLicense()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddDays(90)
        }, "test-user");

        var license = issueResult.License!;

        // Revoke the license
        await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "Test revocation"
        }, "admin-user");

        // Act - attempt renewal
        var renewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = license.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        // Assert
        Assert.IsFalse(renewResult.Success);
        Assert.AreEqual("license_revoked", renewResult.ErrorCode);
    }

    [TestMethod]
    public async Task RevokeLicense_CannotUpgradeRevokedLicense()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var license = issueResult.License!;

        // Revoke the license
        await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "Test revocation"
        }, "admin-user");

        // Act - attempt upgrade
        var upgradeResult = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = license.Id,
            NewTier = "Professional"
        }, "test-user");

        // Assert
        Assert.IsFalse(upgradeResult.Success);
        Assert.AreEqual("license_revoked", upgradeResult.ErrorCode);
    }

    [TestMethod]
    public async Task RevokeLicense_CannotDowngradeRevokedLicense()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Professional",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var license = issueResult.License!;

        // Revoke the license
        await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "Test revocation"
        }, "admin-user");

        // Act - attempt downgrade
        var downgradeResult = await _licenseService.DowngradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = license.Id,
            NewTier = "Standard"
        }, "test-user");

        // Assert
        Assert.IsFalse(downgradeResult.Success);
        Assert.AreEqual("license_revoked", downgradeResult.ErrorCode);
    }

    [TestMethod]
    public async Task RevokeLicense_PreservesTokenForAudit()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var license = issueResult.License!;
        var originalToken = license.SignedToken;

        // Act
        var revokeResult = await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = license.Id,
            Reason = "Test revocation"
        }, "admin-user");

        // Assert - token should be preserved for audit trail
        Assert.IsNotNull(revokeResult.License!.SignedToken);
        Assert.AreEqual(originalToken, revokeResult.License.SignedToken);
    }

    #endregion

    #region Revocation of Different Statuses

    [TestMethod]
    public async Task RevokeLicense_CanRevokeRenewedLicense()
    {
        // Arrange - create and renew a license
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddDays(30)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Renew the license
        var renewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = originalLicense.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var renewedLicense = renewResult.License!;

        // Act - revoke the renewed license (not the original)
        var revokeResult = await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = renewedLicense.Id,
            Reason = "Customer cancelled subscription"
        }, "admin-user");

        // Assert
        Assert.IsTrue(revokeResult.Success);
        Assert.AreEqual(LicenseStatus.Revoked, revokeResult.License!.Status);
    }

    [TestMethod]
    public async Task RevokeLicense_OriginalStillRenewedAfterChildRevoked()
    {
        // Arrange - create and renew a license
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddDays(30)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Renew the license
        var renewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = originalLicense.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var renewedLicense = renewResult.License!;

        // Act - revoke the renewed license
        await _licenseService.RevokeLicenseAsync(new RevokeLicenseRequest
        {
            LicenseId = renewedLicense.Id,
            Reason = "Customer cancelled subscription"
        }, "admin-user");

        // Assert - original should still be marked as Renewed (not Revoked)
        var original = await _licenseStore.GetByIdAsync(originalLicense.Id);
        Assert.AreEqual(LicenseStatus.Renewed, original!.Status);
    }

    #endregion
}
