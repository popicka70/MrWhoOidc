using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using LicensingService.Core.Crypto;
using LicensingService.Core.Entities;
using LicensingService.Core.Services;
using LicensingService.Core.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace LicensingService.Tests.Services;

[TestClass]
public class LicenseValidationServiceTests
{
    private Mock<ISigningKeyService> _signingKeyServiceMock = null!;
    private Mock<ILicenseStore> _licenseStoreMock = null!;
    private Mock<ILogger<LicenseValidationService>> _loggerMock = null!;
    private IConfiguration _configuration = null!;
    private LicenseValidationService _validationService = null!;
    private ECDsa _key = null!;
    private string _kid = null!;

    [TestInitialize]
    public void Setup()
    {
        _signingKeyServiceMock = new Mock<ISigningKeyService>();
        _licenseStoreMock = new Mock<ILicenseStore>();
        _loggerMock = new Mock<ILogger<LicenseValidationService>>();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Licensing:Issuer"] = "TestLicensingService"
            })
            .Build();

        // Create a test key
        _key = EcdsaKeyHelper.GenerateP256Key();
        _kid = Guid.NewGuid().ToString("N")[..16];

        // Setup default signing key mock
        SetupSigningKeyMock(_key, _kid);

        _validationService = new LicenseValidationService(
            _signingKeyServiceMock.Object,
            _configuration,
            _loggerMock.Object,
            _licenseStoreMock.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _key?.Dispose();
    }

    #region ValidateAsync Tests

    [TestMethod]
    public async Task ValidateAsync_WithValidToken_ReturnsSuccess()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var token = CreateValidToken(tokenId, customerId, productId, "Standard", "Site");

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(tokenId, result.TokenId);
        Assert.AreEqual(customerId, result.CustomerIdentifier);
        Assert.AreEqual(productId, result.ProductIdentifier);
        Assert.AreEqual("Standard", result.Tier);
        Assert.AreEqual("Site", result.Scope);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public async Task ValidateAsync_WithExpiredToken_ReturnsFailure()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var token = CreateExpiredToken(tokenId, customerId, productId);

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("token_expired", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateAsync_WithInvalidSignature_ReturnsFailure()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();

        // Create token with a different key
        using var differentKey = EcdsaKeyHelper.GenerateP256Key();
        var token = CreateTokenWithKey(tokenId, customerId, productId, differentKey, "different-kid");

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_signature", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateAsync_WithEmptyToken_ReturnsFailure()
    {
        // Act
        var result = await _validationService.ValidateAsync("");

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("missing_token", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateAsync_WithNullToken_ReturnsFailure()
    {
        // Act
        var result = await _validationService.ValidateAsync(null!);

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("missing_token", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateAsync_WithMalformedToken_ReturnsFailure()
    {
        // Arrange
        var token = "not.a.valid.jwt";

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.IsNotNull(result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateAsync_WithNoSigningKeys_ReturnsFailure()
    {
        // Arrange
        _signingKeyServiceMock.Setup(x => x.GetPublicKeysAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(ECDsa Key, string Kid, string Algorithm)>());

        var token = CreateValidToken("jti", "sub", "aud", "Standard", "Site");

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("no_keys", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateAsync_WithTokenNotYetValid_ReturnsFailure()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var token = CreateFutureToken(tokenId, customerId, productId);

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("token_not_yet_valid", result.ErrorCode);
    }

    #endregion

    #region ValidateForProductAsync Tests

    [TestMethod]
    public async Task ValidateForProductAsync_WithMatchingProduct_ReturnsSuccess()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var token = CreateValidToken(tokenId, customerId, productId, "Enterprise", "Global");

        // Act
        var result = await _validationService.ValidateForProductAsync(token, productId);

        // Assert
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(productId, result.ProductIdentifier);
        Assert.AreEqual("Enterprise", result.Tier);
    }

    [TestMethod]
    public async Task ValidateForProductAsync_WithMismatchedProduct_ReturnsFailure()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var differentProductId = Guid.NewGuid().ToString();
        var token = CreateValidToken(tokenId, customerId, productId, "Standard", "Site");

        // Act
        var result = await _validationService.ValidateForProductAsync(token, differentProductId);

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_audience", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateForProductAsync_WithEmptyProductId_ReturnsFailure()
    {
        // Arrange
        var token = CreateValidToken("jti", "sub", "aud", "Standard", "Site");

        // Act
        var result = await _validationService.ValidateForProductAsync(token, "");

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_product", result.ErrorCode);
    }

    #endregion

    #region Database Check Tests

    [TestMethod]
    public async Task ValidateAsync_WithDatabaseCheck_ReturnsLicenseStatus()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var token = CreateValidToken(tokenId, customerId, productId, "Standard", "Site");

        var license = new License
        {
            Id = Guid.NewGuid(),
            TokenId = tokenId,
            Status = LicenseStatus.Active
        };

        _licenseStoreMock.Setup(x => x.GetByTokenIdAsync(tokenId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(license);

        // Act
        var result = await _validationService.ValidateAsync(token, checkDatabase: true);

        // Assert
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("Active", result.DatabaseStatus);
        Assert.IsFalse(result.IsRevoked);
    }

    [TestMethod]
    public async Task ValidateAsync_WithRevokedLicense_ReturnsRevokedStatus()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var token = CreateValidToken(tokenId, customerId, productId, "Standard", "Site");

        var license = new License
        {
            Id = Guid.NewGuid(),
            TokenId = tokenId,
            Status = LicenseStatus.Revoked
        };

        _licenseStoreMock.Setup(x => x.GetByTokenIdAsync(tokenId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(license);

        // Act
        var result = await _validationService.ValidateAsync(token, checkDatabase: true);

        // Assert
        Assert.IsTrue(result.IsValid); // Token itself is valid
        Assert.AreEqual("Revoked", result.DatabaseStatus);
        Assert.IsTrue(result.IsRevoked);
        Assert.IsFalse(result.IsActive); // But license is not active
    }

    [TestMethod]
    public async Task ValidateAsync_WithLicenseNotInDatabase_StillReturnsValid()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var token = CreateValidToken(tokenId, customerId, productId, "Standard", "Site");

        _licenseStoreMock.Setup(x => x.GetByTokenIdAsync(tokenId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((License?)null);

        // Act
        var result = await _validationService.ValidateAsync(token, checkDatabase: true);

        // Assert
        Assert.IsTrue(result.IsValid);
        Assert.IsNull(result.DatabaseStatus);
    }

    [TestMethod]
    public async Task ValidateAsync_WithoutDatabaseCheck_DoesNotQueryDatabase()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var token = CreateValidToken(tokenId, "sub", "aud", "Standard", "Site");

        // Act
        await _validationService.ValidateAsync(token, checkDatabase: false);

        // Assert
        _licenseStoreMock.Verify(
            x => x.GetByTokenIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region Claims Extraction Tests

    [TestMethod]
    public async Task ValidateAsync_ExtractsTierAndScopeClaims()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var token = CreateValidToken(tokenId, customerId, productId, "Enterprise", "Global");

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("Enterprise", result.Tier);
        Assert.AreEqual("Global", result.Scope);
    }

    [TestMethod]
    public async Task ValidateAsync_ExtractsValidityDates()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var token = CreateValidToken(tokenId, customerId, productId, "Standard", "Site");

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(result.ValidFrom);
        Assert.IsNotNull(result.ValidUntil);
        Assert.IsTrue(result.ValidFrom < result.ValidUntil);
    }

    [TestMethod]
    public async Task ValidateAsync_CalculatesDaysUntilExpiry()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var token = CreateValidToken(tokenId, customerId, productId, "Standard", "Site");

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(result.DaysUntilExpiry);
        Assert.IsTrue(result.DaysUntilExpiry > 0);
    }

    [TestMethod]
    public async Task ValidateAsync_WithOptions_ExtractsOptions()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid().ToString();
        var productId = Guid.NewGuid().ToString();
        var options = new Dictionary<string, object>
        {
            { "MaxUsers", 100 },
            { "EnableFeatureX", true }
        };
        var token = CreateTokenWithOptions(tokenId, customerId, productId, options);

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(result.Options);
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public async Task ValidateAsync_WithMultipleActiveKeys_ValidatesSuccessfully()
    {
        // Arrange
        using var key1 = EcdsaKeyHelper.GenerateP256Key();
        using var key2 = EcdsaKeyHelper.GenerateP256Key();
        var kid1 = Guid.NewGuid().ToString("N")[..16];
        var kid2 = Guid.NewGuid().ToString("N")[..16];

        // Setup multiple keys
        var keys = new List<(ECDsa Key, string Kid, string Algorithm)>
        {
            (key1, kid1, "ES256"),
            (key2, kid2, "ES256")
        };
        _signingKeyServiceMock.Setup(x => x.GetPublicKeysAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(keys);

        // Token signed with key2
        var token = CreateTokenWithKey("jti", "sub", "aud", key2, kid2);

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidateAsync_WithWrongIssuer_ReturnsFailure()
    {
        // Arrange
        var tokenId = Guid.NewGuid().ToString("N");
        var token = CreateTokenWithIssuer(tokenId, "sub", "aud", "WrongIssuer");

        // Act
        var result = await _validationService.ValidateAsync(token);

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_issuer", result.ErrorCode);
    }

    #endregion

    #region Helper Methods

    private void SetupSigningKeyMock(ECDsa key, string kid)
    {
        var keys = new List<(ECDsa Key, string Kid, string Algorithm)>
        {
            (key, kid, "ES256")
        };
        _signingKeyServiceMock.Setup(x => x.GetPublicKeysAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(keys);
    }

    private string CreateValidToken(string tokenId, string customerId, string productId, string tier, string scope)
    {
        return CreateToken(tokenId, customerId, productId, tier, scope, _key, _kid,
            DateTime.UtcNow.AddMinutes(-5),
            DateTime.UtcNow.AddDays(30),
            "TestLicensingService");
    }

    private string CreateExpiredToken(string tokenId, string customerId, string productId)
    {
        return CreateToken(tokenId, customerId, productId, "Standard", "Site", _key, _kid,
            DateTime.UtcNow.AddDays(-60),
            DateTime.UtcNow.AddDays(-30),
            "TestLicensingService");
    }

    private string CreateFutureToken(string tokenId, string customerId, string productId)
    {
        return CreateToken(tokenId, customerId, productId, "Standard", "Site", _key, _kid,
            DateTime.UtcNow.AddDays(30),
            DateTime.UtcNow.AddDays(60),
            "TestLicensingService");
    }

    private string CreateTokenWithKey(string tokenId, string customerId, string productId, ECDsa key, string kid)
    {
        return CreateToken(tokenId, customerId, productId, "Standard", "Site", key, kid,
            DateTime.UtcNow.AddMinutes(-5),
            DateTime.UtcNow.AddDays(30),
            "TestLicensingService");
    }

    private string CreateTokenWithIssuer(string tokenId, string customerId, string productId, string issuer)
    {
        return CreateToken(tokenId, customerId, productId, "Standard", "Site", _key, _kid,
            DateTime.UtcNow.AddMinutes(-5),
            DateTime.UtcNow.AddDays(30),
            issuer);
    }

    private string CreateTokenWithOptions(string tokenId, string customerId, string productId, Dictionary<string, object> options)
    {
        var credentials = new SigningCredentials(
            new ECDsaSecurityKey(_key) { KeyId = _kid },
            SecurityAlgorithms.EcdsaSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, tokenId),
            new(JwtRegisteredClaimNames.Sub, customerId),
            new("tier", "Standard"),
            new("scope", "Site"),
            new("options", System.Text.Json.JsonSerializer.Serialize(options))
        };

        var token = new JwtSecurityToken(
            issuer: "TestLicensingService",
            audience: productId,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateToken(
        string tokenId,
        string customerId,
        string productId,
        string tier,
        string scope,
        ECDsa key,
        string kid,
        DateTime notBefore,
        DateTime expires,
        string issuer)
    {
        var credentials = new SigningCredentials(
            new ECDsaSecurityKey(key) { KeyId = kid },
            SecurityAlgorithms.EcdsaSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, tokenId),
            new(JwtRegisteredClaimNames.Sub, customerId),
            new("tier", tier),
            new("scope", scope)
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: productId,
            claims: claims,
            notBefore: notBefore,
            expires: expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    #endregion
}
