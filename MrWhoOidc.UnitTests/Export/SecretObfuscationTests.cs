using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Export;

/// <summary>
/// Tests for secret obfuscation in export operations.
/// </summary>
[TestClass]
public class SecretObfuscationTests
{
    [TestMethod]
    public void ObfuscateSecret_ReturnsMarker_ForNonEmptySecret()
    {
        // Arrange
        var secret = "super-secret-hash-value";

        // Act
        var result = ExportManifest.ObfuscateSecret(secret);

        // Assert
        Assert.AreEqual(ExportManifest.ObfuscatedMarker, result);
    }

    [TestMethod]
    public void ObfuscateSecret_ReturnsNull_ForNullSecret()
    {
        // Act
        var result = ExportManifest.ObfuscateSecret(null);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void ObfuscateSecret_ReturnsNull_ForEmptySecret()
    {
        // Act
        var result = ExportManifest.ObfuscateSecret(string.Empty);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void ObfuscateSecret_ReturnsNull_ForWhitespaceSecret()
    {
        // Act
        var result = ExportManifest.ObfuscateSecret("   ");

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void IsObfuscated_ReturnsTrue_ForObfuscatedMarker()
    {
        // Act
        var result = ExportManifest.IsObfuscated(ExportManifest.ObfuscatedMarker);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void IsObfuscated_ReturnsFalse_ForRegularSecret()
    {
        // Arrange
        var secret = "regular-secret-value";

        // Act
        var result = ExportManifest.IsObfuscated(secret);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void IsObfuscated_ReturnsFalse_ForNull()
    {
        // Act
        var result = ExportManifest.IsObfuscated(null);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void IsObfuscated_ReturnsFalse_ForEmptyString()
    {
        // Act
        var result = ExportManifest.IsObfuscated(string.Empty);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ObfuscatedMarker_HasExpectedValue()
    {
        // Assert
        Assert.AreEqual("***OBFUSCATED***", ExportManifest.ObfuscatedMarker);
    }

    [TestMethod]
    public void ClientSecretHash_IsObfuscated_InObfuscatedMode()
    {
        // Arrange - simulating what ExportService does
        var originalHash = "$argon2id$v=19$m=65536,t=3,p=4$abc123";
        var mode = ExportMode.Obfuscated;

        // Act
        var exportedValue = mode == ExportMode.Obfuscated 
            ? ExportManifest.ObfuscateSecret(originalHash) 
            : originalHash;

        // Assert
        Assert.AreEqual(ExportManifest.ObfuscatedMarker, exportedValue);
        Assert.IsTrue(ExportManifest.IsObfuscated(exportedValue));
    }

    [TestMethod]
    public void ClientSecretHash_IsPreserved_InFullMode()
    {
        // Arrange - simulating what ExportService does
        var originalHash = "$argon2id$v=19$m=65536,t=3,p=4$abc123";
        var mode = ExportMode.Full;

        // Act
        var exportedValue = mode == ExportMode.Full 
            ? originalHash 
            : ExportManifest.ObfuscateSecret(originalHash);

        // Assert
        Assert.AreEqual(originalHash, exportedValue);
        Assert.IsFalse(ExportManifest.IsObfuscated(exportedValue));
    }

    [TestMethod]
    public void IdentityProviderConfig_ClientSecret_ShouldBeObfuscated()
    {
        // Arrange
        var config = new Dictionary<string, object?>
        {
            ["authority"] = "https://login.microsoftonline.com/tenant",
            ["clientId"] = "client-123",
            ["clientSecret"] = "secret-value-here"
        };

        // Act - simulate obfuscation
        var sensitiveKeys = new[] { "clientSecret", "ClientSecret", "client_secret" };
        foreach (var key in sensitiveKeys)
        {
            if (config.ContainsKey(key) && config[key] != null)
            {
                config[key] = ExportManifest.ObfuscatedMarker;
            }
        }

        // Assert
        Assert.AreEqual(ExportManifest.ObfuscatedMarker, config["clientSecret"]);
        Assert.AreEqual("client-123", config["clientId"]); // Non-sensitive preserved
    }

    [TestMethod]
    public void ExportOptions_Default_UsesObfuscatedMode()
    {
        // Act
        var options = ExportOptions.Default;

        // Assert
        Assert.AreEqual(ExportMode.Obfuscated, options.Mode);
    }

    [TestMethod]
    public void ExportOptions_Full_UsesFullMode()
    {
        // Act
        var options = ExportOptions.Full;

        // Assert
        Assert.AreEqual(ExportMode.Full, options.Mode);
    }

    [TestMethod]
    public void MultipleSecrets_AllObfuscated_InObfuscatedMode()
    {
        // Arrange
        var secrets = new[]
        {
            "hash1-abc",
            "hash2-def",
            "hash3-ghi"
        };

        // Act
        var obfuscated = secrets.Select(s => ExportManifest.ObfuscateSecret(s)).ToArray();

        // Assert
        Assert.IsTrue(obfuscated.All(s => s == ExportManifest.ObfuscatedMarker));
    }

    [TestMethod]
    public void ImportDetectsObfuscatedSecret_RequiresReplacement()
    {
        // Arrange - simulating import preview
        var clientDef = new ClientSeedDefinition
        {
            ClientId = "web-app",
            ClientName = "Web App",
            ClientSecretHash = ExportManifest.ObfuscatedMarker
        };

        // Act
        var needsSecretReplacement = ExportManifest.IsObfuscated(clientDef.ClientSecretHash);

        // Assert
        Assert.IsTrue(needsSecretReplacement);
    }

    [TestMethod]
    public void ImportAllowsHashedSecret_NoReplacementNeeded()
    {
        // Arrange - simulating import with full export
        var clientDef = new ClientSeedDefinition
        {
            ClientId = "web-app",
            ClientName = "Web App",
            ClientSecretHash = "$argon2id$v=19$m=65536,t=3,p=4$actualHashValue"
        };

        // Act
        var needsSecretReplacement = ExportManifest.IsObfuscated(clientDef.ClientSecretHash);

        // Assert
        Assert.IsFalse(needsSecretReplacement);
    }
}
