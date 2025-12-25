using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Export;

/// <summary>
/// Tests for identity provider export serialization to JSON format.
/// </summary>
[TestClass]
public class IdentityProviderExportSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void ExportManifest_WithIdentityProvider_SerializesToJson_WithCorrectSchema()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            ExportType = "identity-provider",
            ExportMode = "obfuscated",
            Metadata = new ExportMetadata
            {
                ExportedAt = DateTimeOffset.Parse("2025-12-23T10:00:00Z"),
                ExportedBy = "admin@test.com",
                SourceSystem = "test-server",
                SourceTenant = "acme/azure-ad"
            },
            Data = new SeedManifest
            {
                Version = 1,
                IdentityProviders =
                [
                    new IdentityProviderSeedDefinition
                    {
                        Name = "azure-ad",
                        DisplayName = "Azure AD",
                        Type = "oidc",
                        Enabled = true
                    }
                ]
            }
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        // Assert
        Assert.IsNotNull(json);
        Assert.IsTrue(json.Contains("\"$schema\""));
        Assert.IsTrue(json.Contains("\"exportType\": \"identity-provider\""));
        Assert.IsTrue(json.Contains("\"exportMode\": \"obfuscated\""));
        Assert.IsTrue(json.Contains("\"name\": \"azure-ad\""));
    }

    [TestMethod]
    public void IdentityProviderExportManifest_DeserializesFromJson_Correctly()
    {
        // Arrange
        var json = """
        {
            "$schema": "https://mrwhooidc.io/schemas/export/v1",
            "version": 1,
            "exportType": "identity-provider",
            "exportMode": "full",
            "metadata": {
                "exportedAt": "2025-12-23T10:00:00Z",
                "exportedBy": "admin@test.com",
                "sourceTenant": "acme/azure-ad"
            },
            "data": {
                "version": 1,
                "identityProviders": [
                    {
                        "name": "azure-ad",
                        "displayName": "Azure Active Directory",
                        "type": "oidc",
                        "enabled": true
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(manifest);
        Assert.AreEqual("identity-provider", manifest.ExportType);
        Assert.AreEqual("full", manifest.ExportMode);
        Assert.IsNotNull(manifest.Data);
        Assert.AreEqual(1, manifest.Data.IdentityProviders?.Count);

        var provider = manifest.Data.IdentityProviders?[0];
        Assert.IsNotNull(provider);
        Assert.AreEqual("azure-ad", provider.Name);
        Assert.AreEqual("Azure Active Directory", provider.DisplayName);
        Assert.AreEqual("oidc", provider.Type);
        Assert.IsTrue(provider.Enabled ?? false);
    }

    [TestMethod]
    public void IdentityProviderSeedDefinition_IncludesClaimMappings()
    {
        // Arrange
        var provider = new IdentityProviderSeedDefinition
        {
            Name = "google",
            DisplayName = "Google",
            Type = "oidc",
            Enabled = true,
            ClaimMappings =
            [
                new ClaimMappingSeedDefinition
                {
                    ExternalClaim = "email",
                    LocalClaim = "email"
                },
                new ClaimMappingSeedDefinition
                {
                    ExternalClaim = "name",
                    LocalClaim = "display_name",
                    Transform = "trim"
                },
                new ClaimMappingSeedDefinition
                {
                    ExternalClaim = "picture",
                    LocalClaim = "avatar_url"
                }
            ]
        };

        // Act
        var json = JsonSerializer.Serialize(provider, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<IdentityProviderSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("google", deserialized.Name);
        Assert.IsNotNull(deserialized.ClaimMappings);
        Assert.AreEqual(3, deserialized.ClaimMappings.Count);

        var emailMapping = deserialized.ClaimMappings.FirstOrDefault(m => m.ExternalClaim == "email");
        Assert.IsNotNull(emailMapping);
        Assert.AreEqual("email", emailMapping.LocalClaim);

        var nameMapping = deserialized.ClaimMappings.FirstOrDefault(m => m.ExternalClaim == "name");
        Assert.IsNotNull(nameMapping);
        Assert.AreEqual("trim", nameMapping.Transform);
    }

    [TestMethod]
    public void IdentityProviderSeedDefinition_IncludesOidcConfiguration()
    {
        // Arrange
        var provider = new IdentityProviderSeedDefinition
        {
            Name = "azure-ad",
            DisplayName = "Azure AD",
            Type = "oidc",
            Enabled = true,
            Config = new Dictionary<string, object?>
            {
                ["authority"] = "https://login.microsoftonline.com/tenant-id/v2.0",
                ["clientId"] = "client-app-id",
                ["scopes"] = "openid profile email offline_access"
            }
        };

        // Act
        var json = JsonSerializer.Serialize(provider, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<IdentityProviderSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.IsNotNull(deserialized.Config);
        Assert.IsTrue(deserialized.Config.ContainsKey("authority"));
        Assert.IsTrue(deserialized.Config.ContainsKey("clientId"));
    }

    [TestMethod]
    public void IdentityProviderExport_WithObfuscatedSecret_UsesMarkerInConfig()
    {
        // Arrange
        var provider = new IdentityProviderSeedDefinition
        {
            Name = "secure-idp",
            DisplayName = "Secure IdP",
            Type = "oidc",
            Enabled = true,
            Config = new Dictionary<string, object?>
            {
                ["clientSecret"] = ExportManifest.ObfuscatedMarker
            }
        };

        // Act
        var json = JsonSerializer.Serialize(provider, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<IdentityProviderSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.IsNotNull(deserialized.Config);
        Assert.AreEqual(ExportManifest.ObfuscatedMarker, deserialized.Config["clientSecret"]?.ToString());
    }

    [TestMethod]
    public void IdentityProviderSeedDefinition_IncludesAllProviderTypes()
    {
        // Test OIDC provider
        var oidcProvider = new IdentityProviderSeedDefinition
        {
            Name = "oidc-provider",
            Type = "oidc",
            Enabled = true
        };

        // Test SAML provider
        var samlProvider = new IdentityProviderSeedDefinition
        {
            Name = "saml-provider",
            Type = "saml",
            Enabled = true
        };

        // Act & Assert
        var oidcJson = JsonSerializer.Serialize(oidcProvider, JsonOptions);
        Assert.IsTrue(oidcJson.Contains("\"type\": \"oidc\""));

        var samlJson = JsonSerializer.Serialize(samlProvider, JsonOptions);
        Assert.IsTrue(samlJson.Contains("\"type\": \"saml\""));
    }

    [TestMethod]
    public void IdentityProviderExport_WithChecksum_IncludesChecksumInMetadata()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            ExportType = "identity-provider",
            ExportMode = "full",
            Metadata = new ExportMetadata
            {
                ExportedAt = DateTimeOffset.UtcNow,
                ExportedBy = "admin@test.com",
                SourceTenant = "test/azure-ad",
                Checksum = "sha256-checksum-value"
            },
            Data = new SeedManifest
            {
                Version = 1,
                IdentityProviders =
                [
                    new IdentityProviderSeedDefinition
                    {
                        Name = "azure-ad",
                        Type = "oidc",
                        Enabled = true
                    }
                ]
            }
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized?.Metadata);
        Assert.AreEqual("sha256-checksum-value", deserialized.Metadata.Checksum);
    }

    [TestMethod]
    public void IdentityProviderExport_EmptyClaimMappings_SerializesCorrectly()
    {
        // Arrange
        var provider = new IdentityProviderSeedDefinition
        {
            Name = "minimal-idp",
            DisplayName = "Minimal IdP",
            Type = "oidc",
            Enabled = true,
            ClaimMappings = []
        };

        // Act
        var json = JsonSerializer.Serialize(provider, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<IdentityProviderSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("minimal-idp", deserialized.Name);
        Assert.IsNotNull(deserialized.ClaimMappings);
        Assert.AreEqual(0, deserialized.ClaimMappings.Count);
    }

    [TestMethod]
    public void ClaimMappingSeedDefinition_IncludesAllFields()
    {
        // Arrange
        var mapping = new ClaimMappingSeedDefinition
        {
            ExternalClaim = "groups",
            LocalClaim = "roles",
            Transform = "json_array",
            Order = 1
        };

        // Act
        var json = JsonSerializer.Serialize(mapping, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClaimMappingSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("groups", deserialized.ExternalClaim);
        Assert.AreEqual("roles", deserialized.LocalClaim);
        Assert.AreEqual("json_array", deserialized.Transform);
        Assert.AreEqual(1, deserialized.Order);
    }

    [TestMethod]
    public void IdentityProviderSeedDefinition_IncludesDisplaySettings()
    {
        // Arrange
        var provider = new IdentityProviderSeedDefinition
        {
            Name = "corporate-sso",
            DisplayName = "Corporate SSO",
            Type = "oidc",
            Enabled = true,
            LogoUrl = "https://cdn.example.com/icons/corporate-sso.svg",
            SortOrder = 1
        };

        // Act
        var json = JsonSerializer.Serialize(provider, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<IdentityProviderSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("https://cdn.example.com/icons/corporate-sso.svg", deserialized.LogoUrl);
        Assert.AreEqual(1, deserialized.SortOrder);
    }
}
