using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.UnitTests.Protocols;

[TestClass]
public class OidcClaimsRequestParserTests
{
    [TestMethod]
    public void TryNormalizeClaimsParameter_WithInvalidJson_ReturnsFalseAndErrorDescription()
    {
        // Arrange
        var rawJson = "{invalid";
        var maxBytes = 1000;

        // Act
        var result = OidcClaimsRequestParser.TryNormalizeClaimsParameter(
            rawJson,
            maxBytes,
            out var normalizedJson,
            out var errorDescription);

        // Assert
        Assert.IsFalse(result);
        Assert.AreEqual(string.Empty, normalizedJson);
        Assert.AreEqual("claims parameter is not valid JSON", errorDescription);
    }

    [TestMethod]
    public void TryNormalizeClaimsParameter_WithEmptyString_ReturnsFalseAndErrorDescription()
    {
        // Arrange
        var rawJson = "";
        var maxBytes = 1000;

        // Act
        var result = OidcClaimsRequestParser.TryNormalizeClaimsParameter(
            rawJson,
            maxBytes,
            out var normalizedJson,
            out var errorDescription);

        // Assert
        Assert.IsFalse(result);
        Assert.AreEqual(string.Empty, normalizedJson);
        Assert.AreEqual("claims parameter is empty", errorDescription);
    }

    [TestMethod]
    public void TryNormalizeClaimsParameter_ExceedingMaxSize_ReturnsFalseAndErrorDescription()
    {
        // Arrange
        var rawJson = "{\"id_token\": {}}"; // Length is 16. 16 chars * 2 bytes/char = 32 bytes
        var maxBytes = 10;

        // Act
        var result = OidcClaimsRequestParser.TryNormalizeClaimsParameter(
            rawJson,
            maxBytes,
            out var normalizedJson,
            out var errorDescription);

        // Assert
        Assert.IsFalse(result);
        Assert.AreEqual(string.Empty, normalizedJson);
        Assert.AreEqual($"claims parameter exceeds max size ({maxBytes} bytes)", errorDescription);
    }

    [TestMethod]
    public void TryNormalizeClaimsParameter_NotAJsonObject_ReturnsFalseAndErrorDescription()
    {
        // Arrange
        var rawJson = "[1, 2, 3]";
        var maxBytes = 1000;

        // Act
        var result = OidcClaimsRequestParser.TryNormalizeClaimsParameter(
            rawJson,
            maxBytes,
            out var normalizedJson,
            out var errorDescription);

        // Assert
        Assert.IsFalse(result);
        Assert.AreEqual(string.Empty, normalizedJson);
        Assert.AreEqual("claims must be a JSON object", errorDescription);
    }

    [TestMethod]
    public void TryNormalizeClaimsParameter_UnsupportedTopLevelMember_ReturnsFalseAndErrorDescription()
    {
        // Arrange
        var rawJson = "{\"unsupported\": {}}";
        var maxBytes = 1000;

        // Act
        var result = OidcClaimsRequestParser.TryNormalizeClaimsParameter(
            rawJson,
            maxBytes,
            out var normalizedJson,
            out var errorDescription);

        // Assert
        Assert.IsFalse(result);
        Assert.AreEqual(string.Empty, normalizedJson);
        Assert.AreEqual("Unsupported claims top-level member 'unsupported'. Only 'id_token' and 'userinfo' are supported.", errorDescription);
    }

    [TestMethod]
    public void TryNormalizeClaimsParameter_MalformedClaimProperty_ReturnsFalseAndErrorDescription()
    {
        // Arrange
        var rawJson = "{\"id_token\": \"not-an-object\"}";
        var maxBytes = 1000;

        // Act
        var result = OidcClaimsRequestParser.TryNormalizeClaimsParameter(
            rawJson,
            maxBytes,
            out var normalizedJson,
            out var errorDescription);

        // Assert
        Assert.IsFalse(result);
        Assert.AreEqual(string.Empty, normalizedJson);
        Assert.AreEqual("claims.id_token must be a JSON object", errorDescription);
    }

    [TestMethod]
    public void TryNormalizeClaimsParameter_MalformedClaimValue_ReturnsFalseAndErrorDescription()
    {
        // Arrange
        var rawJson = "{\"id_token\": {\"email\": \"not-an-object-or-null\"}}";
        var maxBytes = 1000;

        // Act
        var result = OidcClaimsRequestParser.TryNormalizeClaimsParameter(
            rawJson,
            maxBytes,
            out var normalizedJson,
            out var errorDescription);

        // Assert
        Assert.IsFalse(result);
        Assert.AreEqual(string.Empty, normalizedJson);
        Assert.AreEqual("claims.id_token.email must be null or a JSON object", errorDescription);
    }

    [TestMethod]
    public void TryNormalizeClaimsParameter_UnsupportedMember_ReturnsFalseAndErrorDescription()
    {
        // Arrange
        var rawJson = "{\"id_token\": {\"email\": {\"unsupported\": true}}}";
        var maxBytes = 1000;

        // Act
        var result = OidcClaimsRequestParser.TryNormalizeClaimsParameter(
            rawJson,
            maxBytes,
            out var normalizedJson,
            out var errorDescription);

        // Assert
        Assert.IsFalse(result);
        Assert.AreEqual(string.Empty, normalizedJson);
        Assert.AreEqual("Unsupported member claims.id_token.email.unsupported. Only 'essential', 'value', 'values' are supported.", errorDescription);
    }

    [TestMethod]
    public void TryNormalizeClaimsParameter_EssentialNotBoolean_ReturnsFalseAndErrorDescription()
    {
        // Arrange
        var rawJson = "{\"id_token\": {\"email\": {\"essential\": \"true\"}}}";
        var maxBytes = 1000;

        // Act
        var result = OidcClaimsRequestParser.TryNormalizeClaimsParameter(
            rawJson,
            maxBytes,
            out var normalizedJson,
            out var errorDescription);

        // Assert
        Assert.IsFalse(result);
        Assert.AreEqual(string.Empty, normalizedJson);
        Assert.AreEqual("claims.id_token.email.essential must be a boolean", errorDescription);
    }

    [TestMethod]
    public void TryNormalizeClaimsParameter_ValuesNotArray_ReturnsFalseAndErrorDescription()
    {
        // Arrange
        var rawJson = "{\"id_token\": {\"email\": {\"values\": \"not-an-array\"}}}";
        var maxBytes = 1000;

        // Act
        var result = OidcClaimsRequestParser.TryNormalizeClaimsParameter(
            rawJson,
            maxBytes,
            out var normalizedJson,
            out var errorDescription);

        // Assert
        Assert.IsFalse(result);
        Assert.AreEqual(string.Empty, normalizedJson);
        Assert.AreEqual("claims.id_token.email.values must be an array", errorDescription);
    }

    [TestMethod]
    public void TryNormalizeClaimsParameter_ValidClaims_ReturnsTrue()
    {
        // Arrange
        var rawJson = "{\"id_token\": {\"email\": {\"essential\": true, \"value\": \"test@test.com\"}, \"profile\": null}}";
        var maxBytes = 1000;

        // Act
        var result = OidcClaimsRequestParser.TryNormalizeClaimsParameter(
            rawJson,
            maxBytes,
            out var normalizedJson,
            out var errorDescription);

        // Assert
        Assert.IsTrue(result);
        Assert.IsNull(errorDescription);
        Assert.IsNotNull(normalizedJson);
    }
}
