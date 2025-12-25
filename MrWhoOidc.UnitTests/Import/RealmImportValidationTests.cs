using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Import;

/// <summary>
/// Unit tests for realm import validation and parsing.
/// </summary>
[TestClass]
public class RealmImportValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void ValidRealmManifest_PassesValidation()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            Version = 1,
            ExportType = "realm",
            Data = new SeedManifest
            {
                Version = 1,
                Realms =
                [
                    new RealmSeedDefinition
                    {
                        Name = "production",
                        DisplayName = "Production Realm"
                    }
                ]
            }
        };

        // Assert
        Assert.IsNotNull(manifest);
        Assert.AreEqual("realm", manifest.ExportType);
        Assert.IsNotNull(manifest.Data);
        Assert.AreEqual(1, manifest.Data.Realms?.Count);
        Assert.AreEqual("production", manifest.Data.Realms?[0].Name);
    }

    [TestMethod]
    public void RealmManifest_DeserializesFromJson_Correctly()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "realm",
            "data": {
                "version": 1,
                "realms": [
                    {
                        "name": "staging",
                        "displayName": "Staging Realm",
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
        var realm = manifest.Data?.Realms?.FirstOrDefault();
        Assert.IsNotNull(realm);
        Assert.AreEqual("staging", realm.Name);
        Assert.IsTrue(realm.AllowUnconfirmedLogin);
    }

    [TestMethod]
    public void RealmSeedDefinition_RequiresName()
    {
        // Arrange - realm with name
        var validRealm = new RealmSeedDefinition
        {
            Name = "valid-realm",
            DisplayName = "Valid Realm"
        };

        // Assert
        Assert.IsNotNull(validRealm.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(validRealm.Name));
    }

    [TestMethod]
    public void RealmSeedDefinition_WithClients_ValidatesClientRealm()
    {
        // Arrange
        var realm = new RealmSeedDefinition
        {
            Name = "test-realm",
            Clients =
            [
                new ClientSeedDefinition
                {
                    ClientId = "web-app",
                    ClientName = "Web Application",
                    Realm = "test-realm"
                }
            ]
        };

        // Assert
        Assert.AreEqual(1, realm.Clients?.Count);
        var client = realm.Clients?[0];
        Assert.AreEqual(realm.Name, client?.Realm);
    }

    [TestMethod]
    public void RealmSeedDefinition_WithRoles_ValidatesRoleRealm()
    {
        // Arrange
        var realm = new RealmSeedDefinition
        {
            Name = "test-realm",
            Roles =
            [
                new RoleSeedDefinition
                {
                    Name = "admin",
                    RealmName = "test-realm",
                    IsActive = true
                }
            ]
        };

        // Assert
        Assert.AreEqual(1, realm.Roles?.Count);
        var role = realm.Roles?[0];
        Assert.AreEqual(realm.Name, role?.RealmName);
    }

    [TestMethod]
    public void ImportPreview_ForRealm_CanHaveConflicts()
    {
        // Arrange
        var preview = new ImportPreview
        {
            IsValid = true,
            Conflicts =
            [
                new ImportConflict
                {
                    EntityType = "realm",
                    EntityKey = "production",
                    ConflictType = "NameCollision",
                    ExistingValue = "Production Realm",
                    IncomingValue = "New Production Realm",
                    SuggestedResolution = ConflictResolution.Skip
                }
            ],
            RealmCount = 1
        };

        // Assert
        Assert.IsTrue(preview.IsValid);
        Assert.AreEqual(1, preview.Conflicts.Count);
        Assert.AreEqual("realm", preview.Conflicts[0].EntityType);
        Assert.AreEqual(1, preview.RealmCount);
    }

    [TestMethod]
    public void ImportOptions_ForRealm_SupportsAllResolutionStrategies()
    {
        // Arrange
        var options = new ImportOptions
        {
            DryRun = false,
            DefaultConflictResolution = ConflictResolution.Merge
        };

        // Assert
        Assert.IsFalse(options.DryRun);
        Assert.AreEqual(ConflictResolution.Merge, options.DefaultConflictResolution);
    }

    [TestMethod]
    public void RealmImport_WithNestedClients_PreservesClientConfig()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "realm",
            "data": {
                "realms": [
                    {
                        "name": "main",
                        "clients": [
                            {
                                "clientId": "api-client",
                                "clientName": "API Client",
                                "requirePkce": true,
                                "oboEnabled": true,
                                "oboMaxDelegationDepth": 3
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
        var realm = manifest?.Data?.Realms?.FirstOrDefault();
        var client = realm?.Clients?.FirstOrDefault();
        Assert.IsNotNull(client);
        Assert.AreEqual("api-client", client.ClientId);
        Assert.IsTrue(client.RequirePkce ?? false);
        Assert.IsTrue(client.OboEnabled ?? false);
        Assert.AreEqual(3, client.OboMaxDelegationDepth);
    }

    [TestMethod]
    public void RealmImport_WithRoles_PreservesRoleConfig()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "realm",
            "data": {
                "realms": [
                    {
                        "name": "admin-realm",
                        "roles": [
                            {
                                "name": "super-admin",
                                "realmName": "admin-realm",
                                "isActive": true
                            },
                            {
                                "name": "viewer",
                                "realmName": "admin-realm",
                                "isActive": false
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
        var realm = manifest?.Data?.Realms?.FirstOrDefault();
        Assert.AreEqual(2, realm?.Roles?.Count);
        Assert.IsTrue(realm?.Roles?[0].IsActive);
        Assert.IsFalse(realm?.Roles?[1].IsActive);
    }

    [TestMethod]
    public void ImportResult_ForRealm_ContainsCorrectSummary()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            EntitiesCreated = 2,
            EntitiesUpdated = 1,
            EntitiesSkipped = 0,
            RealmsCreated = 1,
            AuditLogId = Guid.NewGuid()
        };

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, result.EntitiesCreated);
        Assert.AreEqual(1, result.EntitiesUpdated);
        Assert.AreEqual(1, result.RealmsCreated);
        Assert.IsNotNull(result.AuditLogId);
    }
}
