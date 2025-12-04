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
public class LicenseRenewalTests
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

    #region 60-Day Overlap Tests

    [TestMethod]
    public async Task RenewLicense_Creates60DayOverlap()
    {
        // Arrange - create original license expiring in 30 days
        var originalExpiry = DateTimeOffset.UtcNow.AddDays(30);
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = originalExpiry
        }, "test-user");

        Assert.IsTrue(issueResult.Success, $"Issue failed: {issueResult.Error} ({issueResult.ErrorCode})");
        var originalLicense = issueResult.License!;

        // Act - renew for another year
        var newExpiry = originalExpiry.AddYears(1);
        var renewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = originalLicense.Id,
            NewValidUntil = newExpiry
        }, "test-user");

        // Assert
        Assert.IsTrue(renewResult.Success);
        var renewedLicense = renewResult.License!;

        // New license should start 60 days before original expires (but not before now)
        var expectedStart = originalExpiry.AddDays(-60);
        if (expectedStart < DateTimeOffset.UtcNow)
        {
            expectedStart = DateTimeOffset.UtcNow;
        }

        // Allow 1 second tolerance for timing
        Assert.IsTrue(Math.Abs((renewedLicense.ValidFrom - expectedStart).TotalSeconds) < 2,
            $"Expected ValidFrom near {expectedStart}, got {renewedLicense.ValidFrom}");
        Assert.AreEqual(newExpiry, renewedLicense.ValidUntil);
    }

    [TestMethod]
    public async Task RenewLicense_BothLicensesValidDuringOverlap()
    {
        // Arrange - create license expiring in 30 days so new license starts now
        var originalExpiry = DateTimeOffset.UtcNow.AddDays(30);
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = originalExpiry
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var newExpiry = originalExpiry.AddYears(1);
        var renewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = originalLicense.Id,
            NewValidUntil = newExpiry
        }, "test-user");

        // Assert
        var renewedLicense = renewResult.License!;
        var now = DateTimeOffset.UtcNow;

        // Original is marked Renewed but token still valid until expiry
        var updatedOriginal = await _licenseStore.GetByIdAsync(originalLicense.Id);
        Assert.AreEqual(LicenseStatus.Renewed, updatedOriginal!.Status);

        // Original token is still technically valid (not expired yet)
        Assert.IsTrue(now >= updatedOriginal.ValidFrom && now <= updatedOriginal.ValidUntil,
            $"Original should be valid. Now: {now}, ValidFrom: {updatedOriginal.ValidFrom}, ValidUntil: {updatedOriginal.ValidUntil}");

        // New license is active
        Assert.AreEqual(LicenseStatus.Active, renewedLicense.Status);
        
        // With 30-day expiry, 60-day overlap means ValidFrom is clamped to now
        // So both licenses should be valid now
        Assert.IsTrue(now >= renewedLicense.ValidFrom && now <= renewedLicense.ValidUntil,
            $"Renewed should be valid. Now: {now}, ValidFrom: {renewedLicense.ValidFrom}, ValidUntil: {renewedLicense.ValidUntil}");
    }

    #endregion

    #region Parent/Child Linking Tests

    [TestMethod]
    public async Task RenewLicense_LinksToParent()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddDays(90)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var renewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = originalLicense.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        // Assert
        var renewedLicense = renewResult.License!;
        Assert.AreEqual(originalLicense.Id, renewedLicense.ParentLicenseId);
    }

    [TestMethod]
    public async Task RenewLicense_PreservesTierAndScope()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Enterprise",
            Scope = "Global",
            ValidUntil = DateTimeOffset.UtcNow.AddDays(90)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var renewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = originalLicense.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        // Assert
        var renewedLicense = renewResult.License!;
        Assert.AreEqual("Enterprise", renewedLicense.Tier);
        Assert.AreEqual("Global", renewedLicense.Scope);
    }

    #endregion

    #region Status Transition Tests

    [TestMethod]
    public async Task RenewLicense_MarksOriginalAsRenewed()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddDays(90)
        }, "test-user");

        var originalLicense = issueResult.License!;
        Assert.AreEqual(LicenseStatus.Active, originalLicense.Status);

        // Act
        await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = originalLicense.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        // Assert
        var updatedOriginal = await _licenseStore.GetByIdAsync(originalLicense.Id);
        Assert.AreEqual(LicenseStatus.Renewed, updatedOriginal!.Status);
    }

    [TestMethod]
    public async Task RenewLicense_CannotRenewRevokedLicense()
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
        }, "test-user");

        // Act
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
    public async Task RenewLicense_CannotRenewAlreadyRenewedLicense()
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

        // First renewal
        await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = license.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        // Act - try to renew again
        var secondRenewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = license.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(2)
        }, "test-user");

        // Assert
        Assert.IsFalse(secondRenewResult.Success);
        Assert.AreEqual("already_renewed", secondRenewResult.ErrorCode);
    }

    #endregion

    #region Option Modification Tests

    [TestMethod]
    public async Task RenewLicense_PreservesOriginalOptions()
    {
        // Arrange - add option definition first
        await _productStore.AddOptionDefinitionAsync(new ProductOptionDefinition
        {
            Id = GuidHelper.NewId(),
            ProductId = _testProduct.Id,
            OptionKey = "max_users",
            DisplayName = "Maximum Users",
            DataType = OptionDataType.Number
        });

        var originalOptions = new Dictionary<string, object> { { "max_users", 100 } };
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddDays(90),
            Options = originalOptions
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act - renew without option updates
        var renewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = originalLicense.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        // Assert
        Assert.IsTrue(renewResult.Success);
        var renewedLicense = renewResult.License!;
        Assert.IsNotNull(renewedLicense.Options);

        var options = JsonSerializer.Deserialize<Dictionary<string, object>>(renewedLicense.Options);
        Assert.IsNotNull(options);
        Assert.IsTrue(options.ContainsKey("max_users"));
    }

    [TestMethod]
    public async Task RenewLicense_CanUpdateOptions()
    {
        // Arrange - add option definitions
        await _productStore.AddOptionDefinitionAsync(new ProductOptionDefinition
        {
            Id = GuidHelper.NewId(),
            ProductId = _testProduct.Id,
            OptionKey = "max_users",
            DisplayName = "Maximum Users",
            DataType = OptionDataType.Number
        });

        var originalOptions = new Dictionary<string, object> { { "max_users", 100 } };
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddDays(90),
            Options = originalOptions
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act - renew with updated options
        var renewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = originalLicense.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1),
            OptionUpdates = new Dictionary<string, object> { { "max_users", 200 } }
        }, "test-user");

        // Assert
        Assert.IsTrue(renewResult.Success);
        var renewedLicense = renewResult.License!;
        Assert.IsNotNull(renewedLicense.Options);

        var options = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(renewedLicense.Options);
        Assert.IsNotNull(options);
        Assert.AreEqual(200, options["max_users"].GetInt32());
    }

    #endregion

    #region Audit Event Tests

    [TestMethod]
    public async Task RenewLicense_CreatesAuditEvents()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddDays(90)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var renewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = originalLicense.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "renewal-user");

        // Assert
        var renewedLicense = renewResult.License!;

        // Check original license has renewal event
        var originalEvents = await _licenseStore.GetEventsAsync(originalLicense.Id);
        Assert.IsTrue(originalEvents.Any(e => e.EventType == LicenseEventType.Renewed));

        // Check new license has created event
        var newEvents = await _licenseStore.GetEventsAsync(renewedLicense.Id);
        Assert.IsTrue(newEvents.Any(e => e.EventType == LicenseEventType.Created));
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public async Task RenewLicense_NonExistentLicense_ReturnsNotFound()
    {
        // Act
        var result = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = Guid.NewGuid(),
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        // Assert
        Assert.IsFalse(result.Success);
        Assert.AreEqual("license_not_found", result.ErrorCode);
    }

    [TestMethod]
    public async Task RenewLicense_GeneratesNewToken()
    {
        // Arrange
        var issueResult = await _licenseService.IssueLicenseAsync(new IssueLicenseRequest
        {
            CustomerId = _testCustomer.Id,
            ProductId = _testProduct.Id,
            Tier = "Standard",
            ValidUntil = DateTimeOffset.UtcNow.AddDays(90)
        }, "test-user");

        var originalLicense = issueResult.License!;

        // Act
        var renewResult = await _licenseService.RenewLicenseAsync(new RenewLicenseRequest
        {
            LicenseId = originalLicense.Id,
            NewValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        // Assert
        Assert.IsTrue(renewResult.Success);
        Assert.IsNotNull(renewResult.Token);
        Assert.AreNotEqual(originalLicense.SignedToken, renewResult.Token);
        Assert.AreNotEqual(originalLicense.TokenId, renewResult.License!.TokenId);
    }

    #endregion
}
