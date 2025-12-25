using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Export;

/// <summary>
/// Tests for realm export serialization to JSON format.
/// </summary>
[TestClass]
public class RealmExportSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void ExportManifest_WithRealm_SerializesToJson_WithCorrectSchema()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            ExportType = "realm",
            ExportMode = "obfuscated",
            Metadata = new ExportMetadata
            {
                ExportedAt = DateTimeOffset.Parse("2025-12-23T10:00:00Z"),
                ExportedBy = "admin@test.com",
                SourceSystem = "test-server",
                SourceTenant = "acme/main"
            },
            Data = new SeedManifest
            {
                Version = 1,
                Realms =
                [
                    new RealmSeedDefinition
                    {
                        Name = "main",
                        DisplayName = "Main Realm",
                        AllowUnconfirmedLogin = false
                    }
                ]
            }
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        // Assert
        Assert.IsNotNull(json);
        StringAssert.Contains(json, "\"$schema\"");
        StringAssert.Contains(json, "\"exportType\": \"realm\"");
        StringAssert.Contains(json, "\"exportMode\": \"obfuscated\"");
        StringAssert.Contains(json, "\"name\": \"main\"");
    }

    [TestMethod]
    public void RealmExportManifest_DeserializesFromJson_Correctly()
    {
        // Arrange
        var json = """
        {
            "$schema": "https://mrwhooidc.io/schemas/export/v1",
            "version": 1,
            "exportType": "realm",
            "exportMode": "full",
            "metadata": {
                "exportedAt": "2025-12-23T10:00:00Z",
                "exportedBy": "admin@test.com",
                "sourceTenant": "acme/main"
            },
            "data": {
                "version": 1,
                "realms": [
                    {
                        "name": "main",
                        "displayName": "Main Realm",
                        "allowUnconfirmedLogin": true
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(manifest);
        Assert.AreEqual("realm", manifest.ExportType);
        Assert.AreEqual("full", manifest.ExportMode);
        Assert.IsNotNull(manifest.Data);
        Assert.AreEqual(1, manifest.Data.Realms?.Count);
        Assert.AreEqual("main", manifest.Data.Realms?[0].Name);
        Assert.AreEqual("Main Realm", manifest.Data.Realms?[0].DisplayName);
        Assert.IsTrue(manifest.Data.Realms?[0].AllowUnconfirmedLogin ?? false);
    }

    [TestMethod]
    public void RealmSeedDefinition_IncludesAllFields()
    {
        // Arrange
        var realm = new RealmSeedDefinition
        {
            Name = "production",
            DisplayName = "Production Realm",
            AllowUnconfirmedLogin = false,
            Clients =
            [
                new ClientSeedDefinition
                {
                    ClientId = "web-app",
                    ClientName = "Web Application",
                    RequirePkce = true,
                    RequireConsent = true,
                    Realm = "production"
                }
            ],
            Roles =
            [
                new RoleSeedDefinition
                {
                    Name = "admin",
                    RealmName = "production",
                    IsActive = true
                },
                new RoleSeedDefinition
                {
                    Name = "user",
                    RealmName = "production",
                    IsActive = true
                }
            ]
        };

        // Act
        var json = JsonSerializer.Serialize(realm, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<RealmSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("production", deserialized.Name);
        Assert.AreEqual("Production Realm", deserialized.DisplayName);
        Assert.IsFalse(deserialized.AllowUnconfirmedLogin);
        Assert.AreEqual(1, deserialized.Clients?.Count);
        Assert.AreEqual("web-app", deserialized.Clients?[0].ClientId);
        Assert.AreEqual(2, deserialized.Roles?.Count);
    }

    [TestMethod]
    public void RealmExport_WithClients_IncludesClientDetails()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            ExportType = "realm",
            ExportMode = "full",
            Metadata = new ExportMetadata
            {
                ExportedAt = DateTimeOffset.UtcNow,
                ExportedBy = "system",
                SourceTenant = "test/realm1"
            },
            Data = new SeedManifest
            {
                Version = 1,
                Realms =
                [
                    new RealmSeedDefinition
                    {
                        Name = "realm1",
                        DisplayName = "Test Realm",
                        Clients =
                        [
                            new ClientSeedDefinition
                            {
                                ClientId = "api-client",
                                ClientName = "API Client",
                                RequirePar = true,
                                OboEnabled = true,
                                AllowedScopes = ["openid", "profile", "api"],
                                AllowedLoginRedirectUris = ["https://app.test.com/callback"]
                            }
                        ]
                    }
                ]
            }
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized?.Data?.Realms);
        var realm = deserialized.Data.Realms[0];
        Assert.AreEqual(1, realm.Clients?.Count);

        var client = realm.Clients?[0];
        Assert.IsNotNull(client);
        Assert.AreEqual("api-client", client.ClientId);
        Assert.IsTrue(client.RequirePar ?? false);
        Assert.IsTrue(client.OboEnabled ?? false);
        Assert.AreEqual(3, client.AllowedScopes?.Count);
    }

    [TestMethod]
    public void RealmExport_WithRoles_IncludesRoleDetails()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            ExportType = "realm",
            ExportMode = "obfuscated",
            Metadata = new ExportMetadata
            {
                ExportedAt = DateTimeOffset.UtcNow,
                ExportedBy = "admin@test.com",
                SourceTenant = "acme/realm1"
            },
            Data = new SeedManifest
            {
                Version = 1,
                Realms =
                [
                    new RealmSeedDefinition
                    {
                        Name = "realm1",
                        Roles =
                        [
                            new RoleSeedDefinition
                            {
                                Name = "super-admin",
                                RealmName = "realm1",
                                IsActive = true
                            },
                            new RoleSeedDefinition
                            {
                                Name = "read-only",
                                RealmName = "realm1",
                                IsActive = false
                            }
                        ]
                    }
                ]
            }
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized?.Data?.Realms);
        var realm = deserialized.Data.Realms[0];
        Assert.AreEqual(2, realm.Roles?.Count);
        Assert.AreEqual("super-admin", realm.Roles?[0].Name);
        Assert.IsTrue(realm.Roles?[0].IsActive);
        Assert.IsFalse(realm.Roles?[1].IsActive);
    }

    [TestMethod]
    public void RealmExport_WithChecksum_IncludesChecksumInMetadata()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            ExportType = "realm",
            ExportMode = "full",
            Metadata = new ExportMetadata
            {
                ExportedAt = DateTimeOffset.UtcNow,
                ExportedBy = "admin@test.com",
                SourceTenant = "test/realm1",
                Checksum = "abc123def456"
            },
            Data = new SeedManifest
            {
                Version = 1,
                Realms = [new RealmSeedDefinition { Name = "realm1" }]
            }
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized?.Metadata);
        Assert.AreEqual("abc123def456", deserialized.Metadata.Checksum);
    }

    [TestMethod]
    public void RealmExport_EmptyCollections_SerializesCorrectly()
    {
        // Arrange
        var realm = new RealmSeedDefinition
        {
            Name = "empty-realm",
            DisplayName = "Empty Realm",
            Clients = [],
            Roles = []
        };

        // Act
        var json = JsonSerializer.Serialize(realm, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<RealmSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("empty-realm", deserialized.Name);
        Assert.IsNotNull(deserialized.Clients);
        Assert.AreEqual(0, deserialized.Clients.Count);
        Assert.IsNotNull(deserialized.Roles);
        Assert.AreEqual(0, deserialized.Roles.Count);
    }
}
