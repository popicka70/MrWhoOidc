using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using LicensingService.Core.Crypto;
using LicensingService.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LicensingService.Tests.Services;

[TestClass]
public class LicenseTokenGeneratorTests
{
    private ILicenseTokenGenerator _generator = null!;
    private ECDsa _signingKey = null!;
    private string _kid = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a test signing key
        _signingKey = EcdsaKeyHelper.GenerateP256Key();
        _kid = Guid.NewGuid().ToString("N")[..16];

        var mockSigningKeyService = new TestSigningKeyService(_signingKey, _kid);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Licensing:Issuer"] = "TestLicensingService"
            })
            .Build();

        _generator = new LicenseTokenGenerator(mockSigningKeyService, configuration);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _signingKey?.Dispose();
    }

    [TestMethod]
    public async Task GenerateAsync_CreatesValidJwt_WithRequiredClaims()
    {
        // Arrange
        var request = new GenerateLicenseTokenRequest
        {
            CustomerIdentifier = "ACME-001",
            ProductIdentifier = "mrwho-oidc",
            Tier = "professional",
            Scope = "per-server",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };

        // Act
        var result = await _generator.GenerateAsync(request);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsFalse(string.IsNullOrEmpty(result.Token));
        Assert.IsFalse(string.IsNullOrEmpty(result.TokenId));
        Assert.AreEqual(_kid, result.Kid);

        // Verify JWT structure
        var handler = new JwtSecurityTokenHandler();
        Assert.IsTrue(handler.CanReadToken(result.Token));

        var jwt = handler.ReadJwtToken(result.Token);

        // Check claims
        Assert.AreEqual("TestLicensingService", jwt.Issuer);
        Assert.AreEqual("ACME-001", jwt.Subject);
        Assert.IsTrue(jwt.Audiences.Contains("mrwho-oidc"));
        Assert.AreEqual("professional", jwt.Claims.First(c => c.Type == "tier").Value);
        Assert.AreEqual("per-server", jwt.Claims.First(c => c.Type == "scope").Value);
        Assert.AreEqual(result.TokenId, jwt.Id);
    }

    [TestMethod]
    public async Task GenerateAsync_IncludesOptions_WhenProvided()
    {
        // Arrange
        var options = new Dictionary<string, object>
        {
            ["max_users"] = 100,
            ["enable_sso"] = true,
            ["custom_branding"] = "enabled"
        };

        var request = new GenerateLicenseTokenRequest
        {
            CustomerIdentifier = "ACME-001",
            ProductIdentifier = "mrwho-oidc",
            Tier = "enterprise",
            Scope = "unlimited",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1),
            Options = options
        };

        // Act
        var result = await _generator.GenerateAsync(request);

        // Assert
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(result.Token);

        var optionsClaim = jwt.Claims.FirstOrDefault(c => c.Type == "options");
        Assert.IsNotNull(optionsClaim);
        Assert.IsTrue(optionsClaim.Value.Contains("max_users"));
        Assert.IsTrue(optionsClaim.Value.Contains("enable_sso"));
    }

    [TestMethod]
    public async Task GenerateAsync_TokenIsVerifiable_WithPublicKey()
    {
        // Arrange
        var request = new GenerateLicenseTokenRequest
        {
            CustomerIdentifier = "ACME-001",
            ProductIdentifier = "mrwho-oidc",
            Tier = "community",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };

        // Act
        var result = await _generator.GenerateAsync(request);

        // Assert - verify signature with public key
        var handler = new JwtSecurityTokenHandler();
        var validationParameters = new TokenValidationParameters
        {
            ValidIssuer = "TestLicensingService",
            ValidAudience = "mrwho-oidc",
            IssuerSigningKey = new ECDsaSecurityKey(_signingKey),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5)
        };

        var principal = handler.ValidateToken(result.Token, validationParameters, out var validatedToken);

        Assert.IsNotNull(principal);
        Assert.IsNotNull(validatedToken);
        Assert.IsInstanceOfType(validatedToken, typeof(JwtSecurityToken));
    }

    [TestMethod]
    public async Task GenerateAsync_SetsCorrectDates()
    {
        // Arrange
        var validFrom = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var validUntil = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var request = new GenerateLicenseTokenRequest
        {
            CustomerIdentifier = "ACME-001",
            ProductIdentifier = "mrwho-oidc",
            Tier = "professional",
            Scope = "default",
            ValidFrom = validFrom,
            ValidUntil = validUntil
        };

        // Act
        var result = await _generator.GenerateAsync(request);

        // Assert
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(result.Token);

        Assert.IsTrue(Math.Abs((validFrom.UtcDateTime - jwt.ValidFrom).TotalSeconds) < 2);
        Assert.IsTrue(Math.Abs((validUntil.UtcDateTime - jwt.ValidTo).TotalSeconds) < 2);
    }

    [TestMethod]
    public async Task GenerateAsync_GeneratesUniqueTokenIds()
    {
        // Arrange
        var request = new GenerateLicenseTokenRequest
        {
            CustomerIdentifier = "ACME-001",
            ProductIdentifier = "mrwho-oidc",
            Tier = "community",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };

        // Act
        var result1 = await _generator.GenerateAsync(request);
        var result2 = await _generator.GenerateAsync(request);
        var result3 = await _generator.GenerateAsync(request);

        // Assert
        Assert.AreNotEqual(result1.TokenId, result2.TokenId);
        Assert.AreNotEqual(result2.TokenId, result3.TokenId);
        Assert.AreNotEqual(result1.TokenId, result3.TokenId);
    }

    [TestMethod]
    public async Task GenerateAsync_UsesES256Algorithm()
    {
        // Arrange
        var request = new GenerateLicenseTokenRequest
        {
            CustomerIdentifier = "ACME-001",
            ProductIdentifier = "mrwho-oidc",
            Tier = "community",
            Scope = "default",
            ValidFrom = DateTimeOffset.UtcNow,
            ValidUntil = DateTimeOffset.UtcNow.AddYears(1)
        };

        // Act
        var result = await _generator.GenerateAsync(request);

        // Assert
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(result.Token);

        Assert.AreEqual("ES256", jwt.Header.Alg);
        Assert.AreEqual(_kid, jwt.Header.Kid);
    }

    /// <summary>
    /// Test implementation of ISigningKeyService for unit testing.
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

        public Task<(ECDsa Key, string Kid)> GetActiveSigningKeyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult((_key, _kid));
        }

        public Task<IReadOnlyList<(ECDsa Key, string Kid, string Algorithm)>> GetPublicKeysAsync(CancellationToken cancellationToken = default)
        {
            var keys = new List<(ECDsa, string, string)> { (_key, _kid, "ES256") };
            return Task.FromResult<IReadOnlyList<(ECDsa, string, string)>>(keys);
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> RotateKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_kid);
    }
}
