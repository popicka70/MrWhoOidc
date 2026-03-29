using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Licensing.Options;
using MrWhoOidc.Auth.Licensing.Validators;

namespace MrWhoOidc.UnitTests.Licensing;

[TestClass]
public sealed class LicenseValidatorTests
{
    // Test public key PEM matching e2e/fixtures/licensing-test-public-key.pem
    private const string TestPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEM/Sc9iNCCXaGirJhKGX+/CNP8Ell
        8/rQgKHevAOYALTb2fltWRzX9S1vjaC8jwgxzy3s6Pzuai6O6c6rqyAhOw==
        -----END PUBLIC KEY-----
        """;

    // Corresponding test private key PEM matching e2e/fixtures/licensing-test-private-key.pem
    private const string TestPrivateKeyPem = """
        -----BEGIN EC PRIVATE KEY-----
        MHcCAQEEIPWN1tm9Zhc65xS1o5IxRUDjmB8d9OBf9YrsjAO01EOzoAoGCCqGSM49
        AwEHoUQDQgAEM/Sc9iNCCXaGirJhKGX+/CNP8Ell8/rQgKHevAOYALTb2fltWRzX
        9S1vjaC8jwgxzy3s6Pzuai6O6c6rqyAhOw==
        -----END EC PRIVATE KEY-----
        """;

    [TestMethod]
    public void SupportedIssuers_ExposeLegacyAndKeyGenValues()
    {
        var supportedIssuers = LicenseValidator.SupportedIssuers.ToList();

        CollectionAssert.Contains(supportedIssuers, "MrWhoOidc-KeyGen");
        CollectionAssert.Contains(supportedIssuers, "MrWhoOidc-License-Authority");
    }

    [TestMethod]
    public void CreateValidationParameters_RegistersAllSupportedIssuers()
    {
        var validator = new LicenseValidator(
            Options.Create(new LicensingOptions()),
            NullLogger<LicenseValidator>.Instance);

        var parameters = InvokeCreateValidationParameters(validator);

        Assert.AreEqual("MrWhoOidc-KeyGen", parameters.ValidIssuer);
        CollectionAssert.AreEquivalent(
            LicenseValidator.SupportedIssuers.ToList(),
            parameters.ValidIssuers?.ToList() ?? new List<string>());
    }

    private static TokenValidationParameters InvokeCreateValidationParameters(LicenseValidator validator)
    {
        var method = typeof(LicenseValidator)
            .GetMethod("CreateValidationParameters", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(method, "LicenseValidator.CreateValidationParameters should exist.");

        var result = method.Invoke(validator, Array.Empty<object?>());
        Assert.IsNotNull(result);

        return (TokenValidationParameters)result;
    }

    [TestMethod]
    public async Task ValidateSignature_AcceptsTokenSignedByAdditionalKey()
    {
        // Arrange: create a validator with the test public key as AdditionalPublicKeyPem
        var options = new LicensingOptions { AdditionalPublicKeyPem = TestPublicKeyPem };
        var validator = new LicenseValidator(
            Options.Create(options),
            NullLogger<LicenseValidator>.Instance);

        // Sign a JWT using the test private key
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(TestPrivateKeyPem);
        var signingCredentials = new SigningCredentials(
            new ECDsaSecurityKey(ecdsa) { KeyId = "licensing-key" },
            SecurityAlgorithms.EcdsaSha256);

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Issuer = "MrWhoOidc-KeyGen",
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddHours(24),
            SigningCredentials = signingCredentials,
            Claims = new Dictionary<string, object>
            {
                ["tier"] = "enterprise+",
                ["organization"] = "Test Org",
                ["features"] = "[\"basic_oidc\",\"basic_admin_ui\"]",
                ["license_scope"] = "platform",
                ["jti"] = Guid.NewGuid().ToString(),
            }
        });

        // Act
        var result = await validator.ValidateSignatureAsync(token);

        // Assert
        Assert.IsTrue(result.IsValid, $"Expected valid signature but got: {result.ErrorCode} - {result.ErrorMessage}");
    }

    [TestMethod]
    public async Task ValidateSignature_RejectsTokenSignedByUnknownKey()
    {
        // Arrange: create a validator WITHOUT extra keys
        var options = new LicensingOptions();
        var validator = new LicenseValidator(
            Options.Create(options),
            NullLogger<LicenseValidator>.Instance);

        // Sign a JWT using the test private key (which the validator doesn't know about)
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(TestPrivateKeyPem);
        var signingCredentials = new SigningCredentials(
            new ECDsaSecurityKey(ecdsa) { KeyId = "licensing-key" },
            SecurityAlgorithms.EcdsaSha256);

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Issuer = "MrWhoOidc-KeyGen",
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddHours(24),
            SigningCredentials = signingCredentials,
            Claims = new Dictionary<string, object>
            {
                ["tier"] = "enterprise+",
                ["organization"] = "Test Org",
                ["features"] = "[\"basic_oidc\"]",
                ["license_scope"] = "platform",
                ["jti"] = Guid.NewGuid().ToString(),
            }
        });

        // Act
        var result = await validator.ValidateSignatureAsync(token);

        // Assert
        Assert.IsFalse(result.IsValid, "Should have rejected token signed by unknown key");
        Assert.AreEqual("invalid_signature", result.ErrorCode);
    }
}
