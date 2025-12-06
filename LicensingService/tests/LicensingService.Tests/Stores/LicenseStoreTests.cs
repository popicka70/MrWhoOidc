using System.Security.Cryptography;
using LicensingService.Core;
using LicensingService.Core.Crypto;
using LicensingService.Core.Entities;
using LicensingService.Core.Persistence;
using LicensingService.Core.Services;
using LicensingService.Core.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LicensingService.Tests.Stores;

[TestClass]
public class LicenseStoreTests
{
    private LicensingDbContext _context = null!;
    private ILicenseStore _store = null!;
    private Customer _testCustomer = null!;
    private LicensedProduct _testProduct = null!;

    [TestInitialize]
    public async Task Setup()
    {
        var options = new DbContextOptionsBuilder<LicensingDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new LicensingDbContext(options);

        // Create test signing key service
        var signingKey = EcdsaKeyHelper.GenerateP256Key();
        var kid = Guid.NewGuid().ToString("N")[..16];
        var mockSigningKeyService = new TestSigningKeyService(signingKey, kid);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Licensing:Issuer"] = "TestLicensingService"
            })
            .Build();

        var tokenGenerator = new LicenseTokenGenerator(mockSigningKeyService, configuration);
        _store = new LicenseStore(_context, tokenGenerator);

        // Seed test data
        _testCustomer = new Customer
        {
            Id = GuidHelper.NewId(),
            Identifier = "TEST-CUSTOMER",
            DisplayName = "Test Customer",
            ContactEmail = "test@example.com",
            Status = "Active",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _context.Customers.Add(_testCustomer);

        _testProduct = new LicensedProduct
        {
            Id = GuidHelper.NewId(),
            Identifier = "test-product",
            DisplayName = "Test Product",
            Status = "Active",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _context.Products.Add(_testProduct);

        await _context.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context?.Dispose();
    }

    [TestMethod]
    public async Task CreateAsync_CreatesLicense_WithGeneratedToken()
    {
        // Arrange
        var license = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "professional",
            Scope = "per-server",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };

        // Act
        var result = await _store.CreateAsync(license, "test-user");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreNotEqual(Guid.Empty, result.Id);
        Assert.IsFalse(string.IsNullOrEmpty(result.TokenId));
        Assert.IsFalse(string.IsNullOrEmpty(result.SignedToken));
        Assert.IsFalse(string.IsNullOrEmpty(result.SigningKeyId));
        Assert.AreEqual(LicenseStatus.Active, result.Status);
        Assert.AreEqual("test-user", result.CreatedBy);
    }

    [TestMethod]
    public async Task CreateAsync_CreatesAuditEvent()
    {
        // Arrange
        var license = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "community",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };

        // Act
        var result = await _store.CreateAsync(license, "admin@example.com");

        // Assert
        var events = await _store.GetEventsAsync(result.Id);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(LicenseEventType.Created, events[0].EventType);
        Assert.AreEqual("admin@example.com", events[0].Actor);
    }

    [TestMethod]
    public async Task GetByIdAsync_ReturnsLicense_WithNavigations()
    {
        // Arrange
        var license = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "enterprise",
            Scope = "unlimited",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };
        var created = await _store.CreateAsync(license, "test-user");

        // Act
        var result = await _store.GetByIdAsync(created.Id);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Customer);
        Assert.IsNotNull(result.Product);
        Assert.AreEqual("TEST-CUSTOMER", result.Customer.Identifier);
        Assert.AreEqual("test-product", result.Product.Identifier);
    }

    [TestMethod]
    public async Task GetByTokenIdAsync_ReturnsCorrectLicense()
    {
        // Arrange
        var license = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "professional",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };
        var created = await _store.CreateAsync(license, "test-user");

        // Act
        var result = await _store.GetByTokenIdAsync(created.TokenId);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(created.Id, result.Id);
    }

    [TestMethod]
    public async Task GetByCustomerIdAsync_ReturnsCustomerLicenses()
    {
        // Arrange
        for (int i = 0; i < 3; i++)
        {
            var license = new License
            {
                CustomerId = _testCustomer.Id,
                Customer = _testCustomer,
                ProductId = _testProduct.Id,
                Product = _testProduct,
                Tier = $"tier-{i}",
                Scope = "default",
                ValidFrom = DateTimeOffset.UtcNow,
                ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
            };
            await _store.CreateAsync(license, "test-user");
        }

        // Act
        var result = await _store.GetByCustomerIdAsync(_testCustomer.Id);

        // Assert
        Assert.AreEqual(3, result.Count);
    }

    [TestMethod]
    public async Task GetByCustomerIdAsync_FiltersbyStatus()
    {
        // Arrange
        var license1 = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "active-license",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };
        await _store.CreateAsync(license1, "test-user");

        var license2 = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "to-revoke",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };
        var created2 = await _store.CreateAsync(license2, "test-user");
        await _store.RevokeAsync(created2.Id, "test-user", "Test revocation");

        // Act
        var activeOnly = await _store.GetByCustomerIdAsync(_testCustomer.Id, LicenseStatus.Active);
        var revokedOnly = await _store.GetByCustomerIdAsync(_testCustomer.Id, LicenseStatus.Revoked);

        // Assert
        Assert.AreEqual(1, activeOnly.Count);
        Assert.AreEqual(1, revokedOnly.Count);
    }

    [TestMethod]
    public async Task RevokeAsync_SetsStatusAndReason()
    {
        // Arrange
        var license = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "professional",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };
        var created = await _store.CreateAsync(license, "test-user");

        // Act
        var result = await _store.RevokeAsync(created.Id, "admin", "Customer requested cancellation");

        // Assert
        Assert.AreEqual(LicenseStatus.Revoked, result.Status);
        Assert.AreEqual("admin", result.RevokedBy);
        Assert.AreEqual("Customer requested cancellation", result.RevocationReason);
        Assert.IsNotNull(result.RevokedAt);
    }

    [TestMethod]
    public async Task RevokeAsync_CreatesAuditEvent()
    {
        // Arrange
        var license = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "professional",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };
        var created = await _store.CreateAsync(license, "test-user");

        // Act
        await _store.RevokeAsync(created.Id, "admin", "Violation of terms");

        // Assert
        var events = await _store.GetEventsAsync(created.Id);
        Assert.AreEqual(2, events.Count); // Created + Revoked
        Assert.IsTrue(events.Any(e => e.EventType == LicenseEventType.Revoked));
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public async Task RevokeAsync_ThrowsIfAlreadyRevoked()
    {
        // Arrange
        var license = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "professional",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };
        var created = await _store.CreateAsync(license, "test-user");
        await _store.RevokeAsync(created.Id, "admin", "First revocation");

        // Act - should throw
        await _store.RevokeAsync(created.Id, "admin", "Second revocation");
    }

    [TestMethod]
    public async Task RenewAsync_CreatesNewLicense_WithParentReference()
    {
        // Arrange
        var originalValidUntil = DateTimeOffset.UtcNow.AddMonths(1);
        var license = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "professional",
            Scope = "per-server",
            ValidFrom = DateTimeOffset.UtcNow.AddYears(-1),
            ValidUntil = originalValidUntil
        };
        var original = await _store.CreateAsync(license, "test-user");

        // Act - renew with 60-day overlap (default)
        var newValidUntil = originalValidUntil.AddYears(1);
        var renewed = await _store.RenewAsync(original.Id, originalValidUntil.AddDays(-60), newValidUntil, "admin");

        // Assert
        Assert.IsNotNull(renewed);
        Assert.AreNotEqual(original.Id, renewed.Id);
        Assert.AreEqual(original.Id, renewed.ParentLicenseId);
        Assert.AreEqual(original.Tier, renewed.Tier);
        Assert.AreEqual(original.Scope, renewed.Scope);
        Assert.AreEqual(newValidUntil, renewed.ValidUntil);

        // Original should be marked as renewed
        var updatedOriginal = await _store.GetByIdAsync(original.Id);
        Assert.AreEqual(LicenseStatus.Renewed, updatedOriginal!.Status);
    }

    [TestMethod]
    public async Task RenewAsync_CreatesAuditEvents()
    {
        // Arrange
        var license = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "professional",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow.AddYears(-1),
            ValidUntil = DateTimeOffset.UtcNow.AddMonths(1)
        };
        var original = await _store.CreateAsync(license, "test-user");

        // Act
        var renewed = await _store.RenewAsync(
            original.Id,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1),
            "admin");

        // Assert - original should have renewal event
        var originalEvents = await _store.GetEventsAsync(original.Id);
        Assert.IsTrue(originalEvents.Any(e => e.EventType == LicenseEventType.Renewed));

        // New license should have created event
        var renewedEvents = await _store.GetEventsAsync(renewed.Id);
        Assert.IsTrue(renewedEvents.Any(e => e.EventType == LicenseEventType.Created));
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidOperationException))]
    public async Task RenewAsync_ThrowsIfRevoked()
    {
        // Arrange
        var license = new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "professional",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow.AddYears(-1),
            ValidUntil = DateTimeOffset.UtcNow.AddMonths(1)
        };
        var original = await _store.CreateAsync(license, "test-user");
        await _store.RevokeAsync(original.Id, "admin", "Cancelled");

        // Act - should throw
        await _store.RenewAsync(original.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1), "admin");
    }

    [TestMethod]
    public async Task SearchAsync_FiltersByCustomerId()
    {
        // Arrange - create another customer
        var customer2 = new Customer
        {
            Id = GuidHelper.NewId(),
            Identifier = "OTHER-CUSTOMER",
            DisplayName = "Other Customer",
            Status = "Active",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _context.Customers.Add(customer2);
        await _context.SaveChangesAsync();

        // Create licenses for both customers
        await _store.CreateAsync(new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "tier1",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        await _store.CreateAsync(new License
        {
            CustomerId = customer2.Id,
            Customer = customer2,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "tier2",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        // Act
        var (items, totalCount) = await _store.SearchAsync(new LicenseSearchCriteria
        {
            CustomerId = _testCustomer.Id
        });

        // Assert
        Assert.AreEqual(1, totalCount);
        Assert.AreEqual(1, items.Count);
        Assert.AreEqual(_testCustomer.Id, items[0].CustomerId);
    }

    [TestMethod]
    public async Task SearchAsync_FiltersByTier()
    {
        // Arrange
        await _store.CreateAsync(new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "community",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        await _store.CreateAsync(new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "enterprise",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        }, "test-user");

        // Act
        var (items, totalCount) = await _store.SearchAsync(new LicenseSearchCriteria
        {
            Tier = "enterprise"
        });

        // Assert
        Assert.AreEqual(1, totalCount);
        Assert.AreEqual("enterprise", items[0].Tier);
    }

    [TestMethod]
    public async Task GetExpiringLicensesAsync_ReturnsLicensesExpiringSoon()
    {
        // Arrange
        // License expiring in 10 days
        await _store.CreateAsync(new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "expiring-soon",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow.AddYears(-1),
            ValidUntil = DateTimeOffset.UtcNow.AddDays(10)
        }, "test-user");

        // License expiring in 100 days
        await _store.CreateAsync(new License
        {
            CustomerId = _testCustomer.Id,
            Customer = _testCustomer,
            ProductId = _testProduct.Id,
            Product = _testProduct,
            Tier = "not-expiring-soon",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow.AddYears(-1),
            ValidUntil = DateTimeOffset.UtcNow.AddDays(100)
        }, "test-user");

        // Act
        var expiring = await _store.GetExpiringLicensesAsync(30);

        // Assert
        Assert.AreEqual(1, expiring.Count);
        Assert.AreEqual("expiring-soon", expiring[0].Tier);
    }

    /// <summary>
    /// Test implementation of ISigningKeyService.
    /// </summary>
    private class TestSigningKeyService : ISigningKeyService
    {
        private readonly ECDsa _key;
        private readonly string _kid;

        public TestSigningKeyService(ECDsa key, string kid)
        {
            _key = key;
            _kid = kid;
        }

        public Task<(ECDsa Key, string Kid)> GetActiveSigningKeyAsync(CancellationToken ct = default)
            => Task.FromResult((_key, _kid));

        public Task<IReadOnlyList<(ECDsa Key, string Kid, string Algorithm)>> GetPublicKeysAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(ECDsa, string, string)>>(new[] { (_key, _kid, "ES256") });

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> RotateKeyAsync(CancellationToken ct = default) => Task.FromResult(_kid);
    }
}
