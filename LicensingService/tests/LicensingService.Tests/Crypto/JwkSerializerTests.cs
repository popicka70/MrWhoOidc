using System.Security.Cryptography;
using System.Text.Json;
using LicensingService.Core.Crypto;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LicensingService.Tests.Crypto;

[TestClass]
public class JwkSerializerTests
{
    [TestMethod]
    public void SerializeEcdsaPublicKeyToJwk_WithP256Key_ReturnsValidJwk()
    {
        // Arrange
        using var ecdsa = EcdsaKeyHelper.GenerateP256Key();
        var kid = "test-key-id";

        // Act
        var jwkJson = JwkSerializer.SerializeEcdsaPublicKeyToJwk(ecdsa, kid);

        // Assert
        Assert.IsNotNull(jwkJson);
        var jwk = JsonDocument.Parse(jwkJson).RootElement;

        Assert.AreEqual("EC", jwk.GetProperty("kty").GetString());
        Assert.AreEqual("sig", jwk.GetProperty("use").GetString());
        Assert.AreEqual("test-key-id", jwk.GetProperty("kid").GetString());
        Assert.AreEqual("ES256", jwk.GetProperty("alg").GetString());
        Assert.AreEqual("P-256", jwk.GetProperty("crv").GetString());
        Assert.IsTrue(jwk.TryGetProperty("x", out _));
        Assert.IsTrue(jwk.TryGetProperty("y", out _));
    }

    [TestMethod]
    public void SerializeEcdsaPublicKeyToJwk_WithCustomAlgorithm_UsesAlgorithm()
    {
        // Arrange
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var kid = "p384-key";

        // Act
        var jwkJson = JwkSerializer.SerializeEcdsaPublicKeyToJwk(ecdsa, kid, "ES384");

        // Assert
        var jwk = JsonDocument.Parse(jwkJson).RootElement;
        Assert.AreEqual("ES384", jwk.GetProperty("alg").GetString());
        Assert.AreEqual("P-384", jwk.GetProperty("crv").GetString());
    }

    [TestMethod]
    public void SerializeToJwks_WithSingleKey_ReturnsValidJwks()
    {
        // Arrange
        using var ecdsa = EcdsaKeyHelper.GenerateP256Key();
        var keys = new List<(ECDsa Key, string Kid, string Algorithm)>
        {
            (ecdsa, "key-1", "ES256")
        };

        // Act
        var jwksJson = JwkSerializer.SerializeToJwks(keys);

        // Assert
        Assert.IsNotNull(jwksJson);
        var jwks = JsonDocument.Parse(jwksJson).RootElement;

        Assert.IsTrue(jwks.TryGetProperty("keys", out var keysArray));
        Assert.AreEqual(1, keysArray.GetArrayLength());

        var key = keysArray[0];
        Assert.AreEqual("EC", key.GetProperty("kty").GetString());
        Assert.AreEqual("key-1", key.GetProperty("kid").GetString());
    }

    [TestMethod]
    public void SerializeToJwks_WithMultipleKeys_ReturnsAllKeys()
    {
        // Arrange
        using var key1 = EcdsaKeyHelper.GenerateP256Key();
        using var key2 = EcdsaKeyHelper.GenerateP256Key();
        using var key3 = EcdsaKeyHelper.GenerateP256Key();

        var keys = new List<(ECDsa Key, string Kid, string Algorithm)>
        {
            (key1, "active-key", "ES256"),
            (key2, "rotated-key-1", "ES256"),
            (key3, "rotated-key-2", "ES256")
        };

        // Act
        var jwksJson = JwkSerializer.SerializeToJwks(keys);

        // Assert
        var jwks = JsonDocument.Parse(jwksJson).RootElement;
        var keysArray = jwks.GetProperty("keys");

        Assert.AreEqual(3, keysArray.GetArrayLength());

        var kids = new List<string>();
        foreach (var key in keysArray.EnumerateArray())
        {
            kids.Add(key.GetProperty("kid").GetString()!);
        }

        Assert.IsTrue(kids.Contains("active-key"));
        Assert.IsTrue(kids.Contains("rotated-key-1"));
        Assert.IsTrue(kids.Contains("rotated-key-2"));
    }

    [TestMethod]
    public void SerializeToJwks_WithEmptyList_ReturnsEmptyKeysArray()
    {
        // Arrange
        var keys = new List<(ECDsa Key, string Kid, string Algorithm)>();

        // Act
        var jwksJson = JwkSerializer.SerializeToJwks(keys);

        // Assert
        var jwks = JsonDocument.Parse(jwksJson).RootElement;
        var keysArray = jwks.GetProperty("keys");

        Assert.AreEqual(0, keysArray.GetArrayLength());
    }

    [TestMethod]
    public void SerializeEcdsaPublicKeyToJwk_DoesNotIncludePrivateKey()
    {
        // Arrange
        using var ecdsa = EcdsaKeyHelper.GenerateP256Key();
        var kid = "test-key";

        // Act
        var jwkJson = JwkSerializer.SerializeEcdsaPublicKeyToJwk(ecdsa, kid);

        // Assert - should not contain private key material (d parameter)
        var jwk = JsonDocument.Parse(jwkJson).RootElement;
        Assert.IsFalse(jwk.TryGetProperty("d", out _), "JWK should not contain private key (d parameter)");
    }

    [TestMethod]
    public void SerializeEcdsaPublicKeyToJwk_XAndYAreBase64UrlEncoded()
    {
        // Arrange
        using var ecdsa = EcdsaKeyHelper.GenerateP256Key();
        var kid = "test-key";

        // Act
        var jwkJson = JwkSerializer.SerializeEcdsaPublicKeyToJwk(ecdsa, kid);

        // Assert
        var jwk = JsonDocument.Parse(jwkJson).RootElement;
        var x = jwk.GetProperty("x").GetString()!;
        var y = jwk.GetProperty("y").GetString()!;

        // Base64url should not contain + or / characters, and no padding =
        Assert.IsFalse(x.Contains('+'), "x should be base64url encoded, not base64");
        Assert.IsFalse(x.Contains('/'), "x should be base64url encoded, not base64");
        Assert.IsFalse(y.Contains('+'), "y should be base64url encoded, not base64");
        Assert.IsFalse(y.Contains('/'), "y should be base64url encoded, not base64");

        // P-256 coordinates should be 32 bytes, base64url encoded = 43 chars
        Assert.IsTrue(x.Length >= 40 && x.Length <= 44, $"x length {x.Length} seems wrong for P-256");
        Assert.IsTrue(y.Length >= 40 && y.Length <= 44, $"y length {y.Length} seems wrong for P-256");
    }

    [TestMethod]
    public void SerializeToJwks_OutputCanBeUsedForTokenValidation()
    {
        // Arrange - create key and serialize
        using var ecdsa = EcdsaKeyHelper.GenerateP256Key();
        var kid = "validation-test-key";
        var keys = new List<(ECDsa Key, string Kid, string Algorithm)> { (ecdsa, kid, "ES256") };

        // Act
        var jwksJson = JwkSerializer.SerializeToJwks(keys);

        // Assert - parse and verify structure matches RFC 7517
        var jwks = JsonDocument.Parse(jwksJson).RootElement;

        // JWKS must have "keys" array
        Assert.IsTrue(jwks.TryGetProperty("keys", out var keysArray));
        Assert.AreEqual(JsonValueKind.Array, keysArray.ValueKind);

        // Each key must have required JWK properties
        foreach (var key in keysArray.EnumerateArray())
        {
            // Required for EC keys
            Assert.IsTrue(key.TryGetProperty("kty", out var kty));
            Assert.AreEqual("EC", kty.GetString());

            Assert.IsTrue(key.TryGetProperty("crv", out _));
            Assert.IsTrue(key.TryGetProperty("x", out _));
            Assert.IsTrue(key.TryGetProperty("y", out _));

            // Recommended for signing keys
            Assert.IsTrue(key.TryGetProperty("kid", out _));
            Assert.IsTrue(key.TryGetProperty("use", out var use));
            Assert.AreEqual("sig", use.GetString());
        }
    }

    [TestMethod]
    public void SerializeToJwks_P521Key_ReturnsCurveP521()
    {
        // Arrange
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP521);
        var keys = new List<(ECDsa Key, string Kid, string Algorithm)>
        {
            (ecdsa, "p521-key", "ES512")
        };

        // Act
        var jwksJson = JwkSerializer.SerializeToJwks(keys);

        // Assert
        var jwks = JsonDocument.Parse(jwksJson).RootElement;
        var key = jwks.GetProperty("keys")[0];
        Assert.AreEqual("P-521", key.GetProperty("crv").GetString());
        Assert.AreEqual("ES512", key.GetProperty("alg").GetString());
    }
}
