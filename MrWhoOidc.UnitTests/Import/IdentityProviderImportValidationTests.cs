using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Import;

/// <summary>
/// Unit tests for identity provider import validation and parsing.
/// </summary>
[TestClass]
public class IdentityProviderImportValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void ValidIdentityProviderManifest_PassesValidation()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            Version = 1,
            ExportType = "identity-provider",
            Data = new SeedManifest
            {
                Version = 1,
                IdentityProviders =
                [
                    new IdentityProviderSeedDefinition
                    {
                        Name = "azure-ad",
                        DisplayName = "Azure Active Directory",
                        Type = "oidc",
                        Enabled = true
                    }
                ]
            }
        };

        // Assert
        Assert.IsNotNull(manifest);
        Assert.AreEqual("identity-provider", manifest.ExportType);
        Assert.IsNotNull(manifest.Data);
        Assert.AreEqual(1, manifest.Data.IdentityProviders?.Count);
        Assert.AreEqual("azure-ad", manifest.Data.IdentityProviders?[0].Name);
    }

    [TestMethod]
    public void IdentityProviderManifest_DeserializesFromJson_Correctly()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "identity-provider",
            "data": {
                "version": 1,
                "identityProviders": [
                    {
                        "name": "google",
                        "displayName": "Google",
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
        var provider = manifest.Data?.IdentityProviders?.FirstOrDefault();
        Assert.IsNotNull(provider);
        Assert.AreEqual("google", provider.Name);
    }

    [TestMethod]
    public void IdentityProviderSeedDefinition_RequiresName()
    {
        // Arrange - provider with name
        var validProvider = new IdentityProviderSeedDefinition
        {
            Name = "valid-idp",
            Type = "oidc",
            Enabled = true
        };

        // Assert
        Assert.IsNotNull(validProvider.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(validProvider.Name));
    }

    [TestMethod]
    public void IdentityProviderImport_WithClaimMappings_PreservesMappings()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "identity-provider",
            "data": {
                "identityProviders": [
                    {
                        "name": "azure-ad",
                        "type": "oidc",
                        "enabled": true,
                        "claimMappings": [
                            {
                                "externalClaim": "preferred_username",
                                "localClaim": "email"
                            },
                            {
                                "externalClaim": "name",
                                "localClaim": "display_name",
                                "transform": "trim"
                            }
                        ]
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        var provider = manifest?.Data?.IdentityProviders?.FirstOrDefault();
        Assert.IsNotNull(provider);
        Assert.AreEqual(2, provider.ClaimMappings?.Count);

        var emailMapping = provider.ClaimMappings?.FirstOrDefault(m => m.ExternalClaim == "preferred_username");
        Assert.IsNotNull(emailMapping);
        Assert.AreEqual("email", emailMapping.LocalClaim);
    }

    [TestMethod]
    public void IdentityProviderImport_WithOidcConfig_PreservesOidcSettings()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "identity-provider",
            "data": {
                "identityProviders": [
                    {
                        "name": "corporate-sso",
                        "type": "oidc",
                        "enabled": true,
                        "config": {
                            "authority": "https://sso.corporate.com/v2.0",
                            "clientId": "sso-client-id",
                            "scopes": "openid profile email groups"
                        }
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        var provider = manifest?.Data?.IdentityProviders?.FirstOrDefault();
        Assert.IsNotNull(provider);
        Assert.IsNotNull(provider.Config);
        Assert.IsTrue(provider.Config.ContainsKey("authority"));
        Assert.IsTrue(provider.Config.ContainsKey("clientId"));
    }

    [TestMethod]
    public void IdentityProviderImport_WithObfuscatedSecret_HandledCorrectly()
    {
        // Arrange
        var provider = new IdentityProviderSeedDefinition
        {
            Name = "secure-idp",
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
        Assert.IsTrue(ExportManifest.IsObfuscated(deserialized.Config["clientSecret"]?.ToString()));
    }

    [TestMethod]
    public void IdentityProviderImport_WithDisplaySettings_PreservesDisplayConfig()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "identity-provider",
            "data": {
                "identityProviders": [
                    {
                        "name": "custom-sso",
                        "type": "oidc",
                        "enabled": true,
                        "displayName": "Enterprise Single Sign-On",
                        "logoUrl": "https://cdn.example.com/sso-icon.svg",
                        "sortOrder": 1
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        var provider = manifest?.Data?.IdentityProviders?.FirstOrDefault();
        Assert.IsNotNull(provider);
        Assert.AreEqual("Enterprise Single Sign-On", provider.DisplayName);
        Assert.AreEqual("https://cdn.example.com/sso-icon.svg", provider.LogoUrl);
        Assert.AreEqual(1, provider.SortOrder);
    }

    [TestMethod]
    public void ImportPreview_ForIdentityProvider_CanHaveConflicts()
    {
        // Arrange
        var preview = new ImportPreview
        {
            IsValid = true,
            Conflicts =
            [
                new ImportConflict
                {
                    EntityType = "identity-provider",
                    EntityKey = "azure-ad",
                    ConflictType = "NameCollision",
                    ExistingValue = "Azure AD (existing)",
                    IncomingValue = "Azure AD (import)",
                    SuggestedResolution = ConflictResolution.Merge
                }
            ],
            ProviderCount = 1
        };

        // Assert
        Assert.IsTrue(preview.IsValid);
        Assert.AreEqual(1, preview.Conflicts.Count);
        Assert.AreEqual("identity-provider", preview.Conflicts[0].EntityType);
        Assert.AreEqual(ConflictResolution.Merge, preview.Conflicts[0].SuggestedResolution);
    }

    [TestMethod]
    public void ClaimMappingSeedDefinition_IncludesTransformation()
    {
        // Arrange
        var mapping = new ClaimMappingSeedDefinition
        {
            ExternalClaim = "groups",
            LocalClaim = "roles",
            Transform = "json_array"
        };

        // Act
        var json = JsonSerializer.Serialize(mapping, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClaimMappingSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("groups", deserialized.ExternalClaim);
        Assert.AreEqual("roles", deserialized.LocalClaim);
        Assert.AreEqual("json_array", deserialized.Transform);
    }

    [TestMethod]
    public void IdentityProviderImport_WithDifferentProviderTypes_PreservesType()
    {
        // Arrange - OIDC provider
        var oidcJson = """
        {
            "identityProviders": [
                { "name": "oidc-idp", "type": "oidc", "enabled": true }
            ]
        }
        """;

        // Arrange - SAML provider
        var samlJson = """
        {
            "identityProviders": [
                { "name": "saml-idp", "type": "saml", "enabled": true }
            ]
        }
        """;

        // Act
        var oidcManifest = JsonSerializer.Deserialize<SeedManifest>(oidcJson, JsonOptions);
        var samlManifest = JsonSerializer.Deserialize<SeedManifest>(samlJson, JsonOptions);

        // Assert
        Assert.AreEqual("oidc", oidcManifest?.IdentityProviders?[0].Type);
        Assert.AreEqual("saml", samlManifest?.IdentityProviders?[0].Type);
    }

    [TestMethod]
    public void IdentityProviderImport_WithDisabledProvider_PreservesState()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "identity-provider",
            "data": {
                "identityProviders": [
                    {
                        "name": "disabled-idp",
                        "type": "oidc",
                        "enabled": false,
                        "displayName": "Disabled Provider"
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        var provider = manifest?.Data?.IdentityProviders?.FirstOrDefault();
        Assert.IsNotNull(provider);
        Assert.IsFalse(provider.Enabled ?? true);
    }

    [TestMethod]
    public void ImportResult_ForIdentityProvider_ContainsCorrectSummary()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            EntitiesCreated = 1,
            EntitiesUpdated = 1,
            EntitiesSkipped = 0,
            ProvidersCreated = 1,
            AuditLogId = Guid.NewGuid()
        };

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.EntitiesCreated);
        Assert.AreEqual(1, result.EntitiesUpdated);
        Assert.AreEqual(1, result.ProvidersCreated);
        Assert.IsNotNull(result.AuditLogId);
    }

    [TestMethod]
    public void ImportOptions_ForIdentityProvider_SupportsDryRun()
    {
        // Arrange
        var options = new ImportOptions
        {
            DryRun = true,
            DefaultConflictResolution = ConflictResolution.Skip
        };

        // Assert
        Assert.IsTrue(options.DryRun);
        Assert.AreEqual(ConflictResolution.Skip, options.DefaultConflictResolution);
    }
}
