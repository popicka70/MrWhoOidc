using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Import;

/// <summary>
/// Unit tests for client import validation and parsing.
/// </summary>
[TestClass]
public class ClientImportValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void ValidClientManifest_PassesValidation()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            Version = 1,
            ExportType = "client",
            Data = new SeedManifest
            {
                Version = 1,
                Clients =
                [
                    new ClientSeedDefinition
                    {
                        ClientId = "web-app",
                        ClientName = "Web Application",
                        Realm = "production"
                    }
                ]
            }
        };

        // Assert
        Assert.IsNotNull(manifest);
        Assert.AreEqual("client", manifest.ExportType);
        Assert.IsNotNull(manifest.Data);
        Assert.AreEqual(1, manifest.Data.Clients?.Count);
        Assert.AreEqual("web-app", manifest.Data.Clients?[0].ClientId);
    }

    [TestMethod]
    public void ClientManifest_DeserializesFromJson_Correctly()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "client",
            "data": {
                "version": 1,
                "clients": [
                    {
                        "clientId": "spa-app",
                        "clientName": "Single Page Application",
                        "realm": "main",
                        "requirePkce": true,
                        "requireConsent": false
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(manifest);
        Assert.AreEqual("client", manifest.ExportType);
        var client = manifest.Data?.Clients?.FirstOrDefault();
        Assert.IsNotNull(client);
        Assert.AreEqual("spa-app", client.ClientId);
        Assert.IsTrue(client.RequirePkce ?? false);
        Assert.IsFalse(client.RequireConsent ?? true);
    }

    [TestMethod]
    public void ClientSeedDefinition_RequiresClientId()
    {
        // Arrange - client with clientId
        var validClient = new ClientSeedDefinition
        {
            ClientId = "valid-client",
            ClientName = "Valid Client"
        };

        // Assert
        Assert.IsNotNull(validClient.ClientId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(validClient.ClientId));
    }

    [TestMethod]
    public void ClientImport_WithIdentityProviders_PreservesAssignments()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "client",
            "data": {
                "clients": [
                    {
                        "clientId": "enterprise-app",
                        "clientName": "Enterprise Application",
                        "identityProviderAssignments": [
                            { "providerName": "azure-ad" },
                            { "providerName": "google" },
                            { "providerName": "okta" }
                        ]
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        var client = manifest?.Data?.Clients?.FirstOrDefault();
        Assert.IsNotNull(client);
        Assert.AreEqual(3, client.IdentityProviderAssignments?.Count);
        Assert.AreEqual("azure-ad", client.IdentityProviderAssignments?[0].ProviderName);
    }

    [TestMethod]
    public void ClientImport_WithScopes_PreservesScopes()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "client",
            "data": {
                "clients": [
                    {
                        "clientId": "api-client",
                        "clientName": "API Client",
                        "allowedScopes": ["openid", "profile", "email", "api.read", "api.write"]
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        var client = manifest?.Data?.Clients?.FirstOrDefault();
        Assert.IsNotNull(client);
        Assert.AreEqual(5, client.AllowedScopes?.Count);
        Assert.IsTrue(client.AllowedScopes?.Contains("openid"));
        Assert.IsTrue(client.AllowedScopes?.Contains("api.write"));
    }

    [TestMethod]
    public void ClientImport_WithRedirectUris_PreservesUris()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "client",
            "data": {
                "clients": [
                    {
                        "clientId": "web-client",
                        "clientName": "Web Client",
                        "allowedLoginRedirectUris": [
                            "https://app.example.com/callback",
                            "https://localhost:3000/callback"
                        ],
                        "allowedLogoutRedirectUris": [
                            "https://app.example.com/logout"
                        ]
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        var client = manifest?.Data?.Clients?.FirstOrDefault();
        Assert.IsNotNull(client);
        Assert.AreEqual(2, client.AllowedLoginRedirectUris?.Count);
        Assert.AreEqual(1, client.AllowedLogoutRedirectUris?.Count);
    }

    [TestMethod]
    public void ClientImport_WithOboSettings_PreservesOboConfig()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "client",
            "data": {
                "clients": [
                    {
                        "clientId": "obo-client",
                        "clientName": "OBO Client",
                        "oboEnabled": true,
                        "oboMaxDelegationDepth": 5,
                        "oboMaxLifetimeMinutes": 120,
                        "oboDpopMode": "Required",
                        "oboAllowedCallers": ["frontend", "mobile"],
                        "oboAllowedScopes": ["api.read", "api.write"]
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        var client = manifest?.Data?.Clients?.FirstOrDefault();
        Assert.IsNotNull(client);
        Assert.IsTrue(client.OboEnabled ?? false);
        Assert.AreEqual(5, client.OboMaxDelegationDepth);
        Assert.AreEqual(120, client.OboMaxLifetimeMinutes);
        Assert.AreEqual("Required", client.OboDpopMode);
        Assert.AreEqual(2, client.OboAllowedCallers?.Count);
    }

    [TestMethod]
    public void ClientImport_WithM2mSettings_PreservesM2mConfig()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "client",
            "data": {
                "clients": [
                    {
                        "clientId": "m2m-client",
                        "clientName": "M2M Client",
                        "m2mAllowedAudiences": ["api://resource1", "api://resource2"],
                        "m2mAccessTokenLifetimeSeconds": 7200
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        var client = manifest?.Data?.Clients?.FirstOrDefault();
        Assert.IsNotNull(client);
        Assert.AreEqual(2, client.M2mAllowedAudiences?.Count);
        Assert.AreEqual(7200, client.M2mAccessTokenLifetimeSeconds);
    }

    [TestMethod]
    public void ImportPreview_ForClient_CanHaveConflicts()
    {
        // Arrange
        var preview = new ImportPreview
        {
            IsValid = true,
            Conflicts =
            [
                new ImportConflict
                {
                    EntityType = "client",
                    EntityKey = "existing-app",
                    ConflictType = "ClientIdCollision",
                    ExistingValue = "Existing App",
                    IncomingValue = "New App with same ClientId",
                    SuggestedResolution = ConflictResolution.Rename
                }
            ],
            ClientCount = 1
        };

        // Assert
        Assert.IsTrue(preview.IsValid);
        Assert.AreEqual(1, preview.Conflicts.Count);
        Assert.AreEqual("client", preview.Conflicts[0].EntityType);
        Assert.AreEqual(ConflictResolution.Rename, preview.Conflicts[0].SuggestedResolution);
    }

    [TestMethod]
    public void ClientImport_WithObfuscatedSecret_HandledCorrectly()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "secure-app",
            ClientName = "Secure Application",
            ClientSecretHash = ExportManifest.ObfuscatedMarker
        };

        // Act
        var json = JsonSerializer.Serialize(client, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClientSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.IsTrue(ExportManifest.IsObfuscated(deserialized.ClientSecretHash));
    }

    [TestMethod]
    public void ClientImport_WithLogoutSettings_PreservesLogoutConfig()
    {
        // Arrange
        var json = """
        {
            "version": 1,
            "exportType": "client",
            "data": {
                "clients": [
                    {
                        "clientId": "enterprise-app",
                        "clientName": "Enterprise Application",
                        "backChannelLogoutUri": "https://app.example.com/backchannel-logout",
                        "backChannelLogoutSessionRequired": true,
                        "frontChannelLogoutUri": "https://app.example.com/front-logout",
                        "frontChannelLogoutSessionRequired": false
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        var client = manifest?.Data?.Clients?.FirstOrDefault();
        Assert.IsNotNull(client);
        Assert.AreEqual("https://app.example.com/backchannel-logout", client.BackChannelLogoutUri);
        Assert.IsTrue(client.BackChannelLogoutSessionRequired ?? false);
        Assert.AreEqual("https://app.example.com/front-logout", client.FrontChannelLogoutUri);
        Assert.IsFalse(client.FrontChannelLogoutSessionRequired ?? true);
    }

    [TestMethod]
    public void ImportResult_ForClient_ContainsCorrectSummary()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            EntitiesCreated = 3,
            EntitiesUpdated = 1,
            EntitiesSkipped = 1,
            ClientsCreated = 3,
            ClientsUpdated = 1,
            ClientsSkipped = 1,
            AuditLogId = Guid.NewGuid()
        };

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, result.EntitiesCreated);
        Assert.AreEqual(3, result.ClientsCreated);
        Assert.IsNotNull(result.AuditLogId);
    }
}
