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
public class LicenseTierChangeTests
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
            CreatedAt = DateTimeOffset.UtcNow,
            OptionDefinitions = new List<ProductOptionDefinition>
            {
                new() { OptionKey = "max_users", DataType = OptionDataType.Number },
                new() { OptionKey = "feature_x", DataType = OptionDataType.Boolean },
                new() { OptionKey = "region", DataType = OptionDataType.String },
                new() { OptionKey = "premium_support", DataType = OptionDataType.Boolean },
                new() { OptionKey = "advanced_analytics", DataType = OptionDataType.Boolean }
            }
        });
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context?.Dispose();
    }

    #region Upgrade Tests

    [TestMethod]
    public async Task UpgradeLicense_CreatesNewLicenseWithNewTier()
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
        var originalLicense = issueResult.License!;

        // Act
        var upgradeResult = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Professional"
        }, "admin-user");

        // Assert
        Assert.IsTrue(upgradeResult.Success, $"Upgrade failed: {upgradeResult.Error}");
        Assert.AreEqual("Professional", upgradeResult.License!.Tier);
        Assert.AreNotEqual(originalLicense.Id, upgradeResult.License.Id);
    }

    [TestMethod]
    public async Task UpgradeLicense_MarksOriginalAsUpgraded()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Professional"
        }, "admin-user");

        // Assert
        var updatedOriginal = await _licenseStore.GetByIdAsync(originalLicense.Id);
        Assert.AreEqual(LicenseStatus.Upgraded, updatedOriginal!.Status);
    }

    [TestMethod]
    public async Task UpgradeLicense_LinksToParent()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var upgradeResult = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Professional"
        }, "admin-user");

        // Assert
        Assert.AreEqual(originalLicense.Id, upgradeResult.License!.ParentLicenseId);
    }

    [TestMethod]
    public async Task UpgradeLicense_PreservesValidityPeriod()
    {
        // Arrange
        var validUntil = DateTimeOffset.UtcNow.AddYears(1);
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = validUntil
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var upgradeResult = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Professional"
        }, "admin-user");

        // Assert - new license keeps same expiry
        Assert.AreEqual(validUntil, upgradeResult.License!.ValidUntil);
    }

    [TestMethod]
    public async Task UpgradeLicense_GeneratesNewToken()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var upgradeResult = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Professional"
        }, "admin-user");

        // Assert
        Assert.IsNotNull(upgradeResult.License!.SignedToken);
        Assert.AreNotEqual(originalLicense.SignedToken, upgradeResult.License.SignedToken);
        Assert.AreNotEqual(originalLicense.TokenId, upgradeResult.License.TokenId);
    }

    [TestMethod]
    public async Task UpgradeLicense_CreatesAuditEvents()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var upgradeResult = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Professional"
        }, "admin-user");

        // Assert - check for upgrade event on original
        var originalEvents = await _context.LicenseEvents
            .Where(e => e.LicenseId == originalLicense.Id && e.EventType == LicenseEventType.Upgraded)
            .ToListAsync();
        Assert.AreEqual(1, originalEvents.Count);
        Assert.AreEqual("admin-user", originalEvents[0].Actor);
    }

    [TestMethod]
    public async Task UpgradeLicense_CanUpdateOptions()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1),
            Options = new Dictionary<string, object> { ["max_users"] = 10 }
        }, "test-user");

        Assert.IsTrue(issueResult.Success, $"Issue failed: {issueResult.Error}");
        var originalLicense = issueResult.License!;
        Assert.IsNotNull(originalLicense.Options, "Original license should have options");

        // Act
        var upgradeResult = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Professional",
            OptionUpdates = new Dictionary<string, object> { ["max_users"] = 50 }
        }, "admin-user");

        // Assert
        Assert.IsTrue(upgradeResult.Success, $"Upgrade failed: {upgradeResult.Error}");
        Assert.IsNotNull(upgradeResult.License!.Options, "Upgraded license should have options");
        var options = JsonSerializer.Deserialize<Dictionary<string, object>>(upgradeResult.License!.Options!);
        Assert.IsNotNull(options);
        Assert.IsTrue(options.ContainsKey("max_users"), "Options should contain max_users");
        Assert.AreEqual(50, ((JsonElement)options["max_users"]).GetInt32());
    }

    [TestMethod]
    public async Task UpgradeLicense_FailsForRevokedLicense()
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

        // Act
        var upgradeResult = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = license.Id,
            NewTier = "Professional"
        }, "admin-user");

        // Assert
        Assert.IsFalse(upgradeResult.Success);
        Assert.AreEqual("license_revoked", upgradeResult.ErrorCode);
    }

    [TestMethod]
    public async Task UpgradeLicense_FailsForNonexistentLicense()
    {
        // Act
        var upgradeResult = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = Guid.NewGuid(),
            NewTier = "Professional"
        }, "admin-user");

        // Assert
        Assert.IsFalse(upgradeResult.Success);
        Assert.AreEqual("license_not_found", upgradeResult.ErrorCode);
    }

    #endregion

    #region Downgrade Tests

    [TestMethod]
    public async Task DowngradeLicense_CreatesNewLicenseWithNewTier()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Enterprise",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var downgradeResult = await _licenseService.DowngradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Standard"
        }, "admin-user");

        // Assert
        Assert.IsTrue(downgradeResult.Success, $"Downgrade failed: {downgradeResult.Error}");
        Assert.AreEqual("Standard", downgradeResult.License!.Tier);
        Assert.AreNotEqual(originalLicense.Id, downgradeResult.License.Id);
    }

    [TestMethod]
    public async Task DowngradeLicense_MarksOriginalAsDowngraded()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Professional",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        await _licenseService.DowngradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Standard"
        }, "admin-user");

        // Assert
        var updatedOriginal = await _licenseStore.GetByIdAsync(originalLicense.Id);
        Assert.AreEqual(LicenseStatus.Downgraded, updatedOriginal!.Status);
    }

    [TestMethod]
    public async Task DowngradeLicense_LinksToParent()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Enterprise",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var downgradeResult = await _licenseService.DowngradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Standard"
        }, "admin-user");

        // Assert
        Assert.AreEqual(originalLicense.Id, downgradeResult.License!.ParentLicenseId);
    }

    [TestMethod]
    public async Task DowngradeLicense_CreatesAuditEvents()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Professional",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        await _licenseService.DowngradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Standard"
        }, "admin-user");

        // Assert
        var downgradeEvents = await _context.LicenseEvents
            .Where(e => e.LicenseId == originalLicense.Id && e.EventType == LicenseEventType.Downgraded)
            .ToListAsync();
        Assert.AreEqual(1, downgradeEvents.Count);
    }

    [TestMethod]
    public async Task DowngradeLicense_CanRemoveOptions()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Enterprise",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1),
            Options = new Dictionary<string, object>
            {
                ["max_users"] = 1000,
                ["premium_support"] = true,
                ["advanced_analytics"] = true
            }
        }, "test-user");

        Assert.IsTrue(issueResult.Success, $"Issue failed: {issueResult.Error}");
        var originalLicense = issueResult.License!;
        Assert.IsNotNull(originalLicense.Options, "Original license should have options");

        // Act - downgrade with reduced options
        var downgradeResult = await _licenseService.DowngradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Standard",
            OptionUpdates = new Dictionary<string, object>
            {
                ["max_users"] = 25,
                ["premium_support"] = false
            }
        }, "admin-user");

        // Assert
        Assert.IsTrue(downgradeResult.Success, $"Downgrade failed: {downgradeResult.Error}");
        Assert.IsNotNull(downgradeResult.License!.Options, "Downgraded license should have options");
        var options = JsonSerializer.Deserialize<Dictionary<string, object>>(downgradeResult.License!.Options!);
        Assert.IsNotNull(options);
        Assert.AreEqual(25, ((JsonElement)options["max_users"]).GetInt32());
        Assert.IsFalse(((JsonElement)options["premium_support"]).GetBoolean());
    }

    [TestMethod]
    public async Task DowngradeLicense_FailsForRevokedLicense()
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

        // Act
        var downgradeResult = await _licenseService.DowngradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = license.Id,
            NewTier = "Standard"
        }, "admin-user");

        // Assert
        Assert.IsFalse(downgradeResult.Success);
        Assert.AreEqual("license_revoked", downgradeResult.ErrorCode);
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public async Task TierChange_PreservesCustomerAndProduct()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var upgradeResult = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Professional"
        }, "admin-user");

        // Assert
        Assert.AreEqual(_testCustomer.Id, upgradeResult.License!.CustomerId);
        Assert.AreEqual(_testProduct.Id, upgradeResult.License.ProductId);
    }

    [TestMethod]
    public async Task TierChange_PreservesScope()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            Scope = "per-server",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var upgradeResult = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Professional"
        }, "admin-user");

        // Assert
        Assert.AreEqual("per-server", upgradeResult.License!.Scope);
    }

    [TestMethod]
    public async Task MultipleTierChanges_MaintainsChain()
    {
        // Arrange - issue, upgrade, then upgrade again
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // First upgrade
        var firstUpgrade = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = originalLicense.Id,
            NewTier = "Professional"
        }, "admin-user");

        // Second upgrade
        var secondUpgrade = await _licenseService.UpgradeLicenseAsync(new ChangeTierRequest
        {
            LicenseId = firstUpgrade.License!.Id,
            NewTier = "Enterprise"
        }, "admin-user");

        // Assert - check chain
        Assert.AreEqual("Enterprise", secondUpgrade.License!.Tier);
        Assert.AreEqual(firstUpgrade.License.Id, secondUpgrade.License.ParentLicenseId);

        var firstUpgradeLicense = await _licenseStore.GetByIdAsync(firstUpgrade.License.Id);
        Assert.AreEqual(LicenseStatus.Upgraded, firstUpgradeLicense!.Status);
        Assert.AreEqual(originalLicense.Id, firstUpgradeLicense.ParentLicenseId);
    }

    #endregion
}
