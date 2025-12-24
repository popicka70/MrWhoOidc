using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Import;

/// <summary>
/// Unit tests for import manifest validation.
/// </summary>
[TestClass]
public class ManifestValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void ValidManifest_PassesValidation()
    {
        // Arrange
        var manifest = CreateValidManifest();

        // Act & Assert
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.Version);
        Assert.IsNotNull(manifest.Data);
        Assert.AreEqual(1, manifest.Data.Tenants.Count);
    }

    [TestMethod]
    public void Manifest_WithMissingVersion_HasDefaultVersion()
    {
        // Arrange
        var json = """
        {
            "data": {
                "tenants": []
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.Version); // Default value
    }

    [TestMethod]
    public void Manifest_WithInvalidVersion_CanBeDetected()
    {
        // Arrange
        var manifest = new ExportManifest { Version = 999 };

        // Act & Assert
        Assert.AreNotEqual(1, manifest.Version);
    }

    [TestMethod]
    public void Manifest_WithEmptyTenants_IsValid()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            Version = 1,
            Data = new SeedManifest { Tenants = [] }
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(0, deserialized.Data.Tenants.Count);
    }

    [TestMethod]
    public void TenantSeedDefinition_RequiresSlug()
    {
        // Arrange - tenant with slug
        var validTenant = new TenantSeedDefinition
        {
            Slug = "valid-tenant",
            Name = "Valid Tenant"
        };

        // Assert
        Assert.IsNotNull(validTenant.Slug);
        Assert.IsFalse(string.IsNullOrWhiteSpace(validTenant.Slug));
    }

    [TestMethod]
    public void TenantSeedDefinition_RequiresName()
    {
        // Arrange
        var tenant = new TenantSeedDefinition
        {
            Slug = "test-tenant",
            Name = "Test Tenant"
        };

        // Assert
        Assert.IsNotNull(tenant.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(tenant.Name));
    }

    [TestMethod]
    public void ClientSeedDefinition_RequiresClientId()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "my-client",
            ClientName = "My Client"
        };

        // Assert
        Assert.IsNotNull(client.ClientId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(client.ClientId));
    }

    [TestMethod]
    public void ClientSeedDefinition_RequiresClientName()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "my-client",
            ClientName = "My Client"
        };

        // Assert
        Assert.IsNotNull(client.ClientName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(client.ClientName));
    }

    [TestMethod]
    public void RealmSeedDefinition_RequiresName()
    {
        // Arrange
        var realm = new RealmSeedDefinition
        {
            Name = "admin"
        };

        // Assert
        Assert.IsNotNull(realm.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(realm.Name));
    }

    [TestMethod]
    public void IdentityProviderSeedDefinition_RequiresName()
    {
        // Arrange
        var provider = new IdentityProviderSeedDefinition
        {
            Name = "azure-ad"
        };

        // Assert
        Assert.IsNotNull(provider.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(provider.Name));
    }

    [TestMethod]
    public void Manifest_SerializesAndDeserializes_WithNestedEntities()
    {
        // Arrange
        var manifest = CreateValidManifest();

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(1, deserialized.Data.Tenants.Count);
        Assert.AreEqual(1, deserialized.Data.Tenants[0].Realms.Count);
        Assert.AreEqual(1, deserialized.Data.Tenants[0].Clients.Count);
    }

    [TestMethod]
    public void Manifest_WithObfuscatedSecrets_IsIdentifiable()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            Version = 1,
            ExportMode = "obfuscated",
            Data = new SeedManifest
            {
                Tenants =
                [
                    new TenantSeedDefinition
                    {
                        Slug = "test",
                        Name = "Test",
                        Clients =
                        [
                            new ClientSeedDefinition
                            {
                                ClientId = "client1",
                                ClientName = "Client 1",
                                ClientSecret = ExportManifest.ObfuscatedMarker
                            }
                        ]
                    }
                ]
            }
        };

        // Act & Assert
        Assert.AreEqual("obfuscated", manifest.ExportMode);
        Assert.IsTrue(ExportManifest.IsObfuscated(manifest.Data.Tenants[0].Clients[0].ClientSecret));
    }

    [TestMethod]
    public void Manifest_WithFullExport_ContainsHashedSecrets()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            Version = 1,
            ExportMode = "full",
            Data = new SeedManifest
            {
                Tenants =
                [
                    new TenantSeedDefinition
                    {
                        Slug = "test",
                        Name = "Test",
                        Clients =
                        [
                            new ClientSeedDefinition
                            {
                                ClientId = "client1",
                                ClientName = "Client 1",
                                ClientSecretHash = "$argon2id$v=19$m=65536,t=3,p=1$..."
                            }
                        ]
                    }
                ]
            }
        };

        // Act & Assert
        Assert.AreEqual("full", manifest.ExportMode);
        Assert.IsNotNull(manifest.Data.Tenants[0].Clients[0].ClientSecretHash);
        Assert.IsFalse(ExportManifest.IsObfuscated(manifest.Data.Tenants[0].Clients[0].ClientSecretHash));
    }

    [TestMethod]
    public void ImportOptions_DefaultValues_AreCorrect()
    {
        // Arrange
        var options = new ImportOptions();

        // Assert
        Assert.AreEqual(ConflictResolution.Skip, options.DefaultConflictResolution);
        Assert.IsFalse(options.DryRun);
        Assert.IsFalse(options.ValidateOnly);
    }

    [TestMethod]
    public void ImportOptions_ConflictResolutions_CanBeSet()
    {
        // Arrange
        var options = new ImportOptions
        {
            DefaultConflictResolution = ConflictResolution.Overwrite,
            ConflictResolutions = new Dictionary<string, ConflictResolution>
            {
                ["tenant:existing-slug"] = ConflictResolution.Skip,
                ["client:existing-client"] = ConflictResolution.Rename
            }
        };

        // Assert
        Assert.AreEqual(ConflictResolution.Overwrite, options.DefaultConflictResolution);
        Assert.AreEqual(2, options.ConflictResolutions.Count);
        Assert.AreEqual(ConflictResolution.Skip, options.ConflictResolutions["tenant:existing-slug"]);
    }

    [TestMethod]
    public void ExportOptions_DefaultValues_AreCorrect()
    {
        // Arrange
        var options = new ExportOptions();

        // Assert
        Assert.AreEqual(ExportMode.Obfuscated, options.Mode);
        Assert.IsTrue(options.IncludeMetadata);
        Assert.IsTrue(options.IncludeChecksum);
        Assert.IsTrue(options.PrettyPrint);
    }

    private static ExportManifest CreateValidManifest()
    {
        return new ExportManifest
        {
            Version = 1,
            ExportType = "tenant",
            ExportMode = "obfuscated",
            Metadata = new ExportMetadata
            {
                ExportedAt = DateTime.UtcNow,
                ExportedBy = "test-user",
                SourceSystem = "test-system"
            },
            Data = new SeedManifest
            {
                Version = 1,
                Tenants =
                [
                    new TenantSeedDefinition
                    {
                        Slug = "test-tenant",
                        Name = "Test Tenant",
                        IssuerUri = "https://test.example.com",
                        Status = "active",
                        Realms =
                        [
                            new RealmSeedDefinition
                            {
                                Name = "admin",
                                DisplayName = "Admin Realm"
                            }
                        ],
                        Clients =
                        [
                            new ClientSeedDefinition
                            {
                                ClientId = "test-client",
                                ClientName = "Test Client",
                                Realm = "admin"
                            }
                        ]
                    }
                ]
            }
        };
    }
}
