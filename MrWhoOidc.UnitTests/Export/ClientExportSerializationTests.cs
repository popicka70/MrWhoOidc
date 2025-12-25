using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Export;

/// <summary>
/// Tests for client export serialization to JSON format.
/// </summary>
[TestClass]
public class ClientExportSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void ExportManifest_WithClient_SerializesToJson_WithCorrectSchema()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            ExportType = "client",
            ExportMode = "obfuscated",
            Metadata = new ExportMetadata
            {
                ExportedAt = DateTimeOffset.Parse("2025-12-23T10:00:00Z"),
                ExportedBy = "admin@test.com",
                SourceSystem = "test-server",
                SourceTenant = "acme/main/web-app"
            },
            Data = new SeedManifest
            {
                Version = 1,
                Clients =
                [
                    new ClientSeedDefinition
                    {
                        ClientId = "web-app",
                        ClientName = "Web Application",
                        Realm = "main"
                    }
                ]
            }
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        // Assert
        Assert.IsNotNull(json);
        StringAssert.Contains(json, "\"$schema\"");
        StringAssert.Contains(json, "\"exportType\": \"client\"");
        StringAssert.Contains(json, "\"exportMode\": \"obfuscated\"");
        StringAssert.Contains(json, "\"clientId\": \"web-app\"");
    }

    [TestMethod]
    public void ClientExportManifest_DeserializesFromJson_Correctly()
    {
        // Arrange
        var json = """
        {
            "$schema": "https://mrwhooidc.io/schemas/export/v1",
            "version": 1,
            "exportType": "client",
            "exportMode": "full",
            "metadata": {
                "exportedAt": "2025-12-23T10:00:00Z",
                "exportedBy": "admin@test.com",
                "sourceTenant": "acme/main/web-app"
            },
            "data": {
                "version": 1,
                "clients": [
                    {
                        "clientId": "web-app",
                        "clientName": "Web Application",
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
        Assert.AreEqual("full", manifest.ExportMode);
        Assert.IsNotNull(manifest.Data);
        Assert.AreEqual(1, manifest.Data.Clients?.Count);

        var client = manifest.Data.Clients?[0];
        Assert.IsNotNull(client);
        Assert.AreEqual("web-app", client.ClientId);
        Assert.AreEqual("Web Application", client.ClientName);
        Assert.AreEqual("main", client.Realm);
        Assert.IsTrue(client.RequirePkce ?? false);
        Assert.IsFalse(client.RequireConsent ?? true);
    }

    [TestMethod]
    public void ClientSeedDefinition_IncludesIdentityProviderAssignments()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "spa-app",
            ClientName = "Single Page Application",
            Realm = "production",
            IdentityProviderAssignments =
            [
                new ClientIdpAssignmentSeedDefinition { ProviderName = "azure-ad" },
                new ClientIdpAssignmentSeedDefinition { ProviderName = "google" },
                new ClientIdpAssignmentSeedDefinition { ProviderName = "github" }
            ],
            AllowedScopes = ["openid", "profile", "email"]
        };

        // Act
        var json = JsonSerializer.Serialize(client, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClientSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("spa-app", deserialized.ClientId);
        Assert.IsNotNull(deserialized.IdentityProviderAssignments);
        Assert.AreEqual(3, deserialized.IdentityProviderAssignments.Count);
        Assert.AreEqual("azure-ad", deserialized.IdentityProviderAssignments[0].ProviderName);
    }

    [TestMethod]
    public void ClientSeedDefinition_IncludesRedirectUris()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "web-client",
            ClientName = "Web Client",
            AllowedLoginRedirectUris =
            [
                "https://app.example.com/callback",
                "https://app.example.com/silent-renew",
                "https://localhost:3000/callback"
            ],
            AllowedLogoutRedirectUris =
            [
                "https://app.example.com/logout",
                "https://localhost:3000/logout"
            ]
        };

        // Act
        var json = JsonSerializer.Serialize(client, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClientSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(3, deserialized.AllowedLoginRedirectUris?.Count);
        Assert.AreEqual(2, deserialized.AllowedLogoutRedirectUris?.Count);
        Assert.IsTrue(deserialized.AllowedLoginRedirectUris?.Contains("https://app.example.com/callback"));
    }

    [TestMethod]
    public void ClientSeedDefinition_IncludesOboSettings()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "api-gateway",
            ClientName = "API Gateway",
            OboEnabled = true,
            OboMaxDelegationDepth = 5,
            OboMaxLifetimeMinutes = 120,
            OboDpopMode = "Required",
            OboAllowedCallers = ["frontend-app", "mobile-app"],
            OboAllowedSourceAudiences = ["api://frontend"],
            OboAllowedTargetAudiences = ["api://backend"],
            OboAllowedScopes = ["api.read", "api.write", "api.admin"]
        };

        // Act
        var json = JsonSerializer.Serialize(client, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClientSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.IsTrue(deserialized.OboEnabled ?? false);
        Assert.AreEqual(5, deserialized.OboMaxDelegationDepth);
        Assert.AreEqual(120, deserialized.OboMaxLifetimeMinutes);
        Assert.AreEqual("Required", deserialized.OboDpopMode);
        Assert.AreEqual(2, deserialized.OboAllowedCallers?.Count);
        Assert.AreEqual(3, deserialized.OboAllowedScopes?.Count);
    }

    [TestMethod]
    public void ClientSeedDefinition_IncludesM2mSettings()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "backend-service",
            ClientName = "Backend Service",
            M2mAllowedAudiences = ["api://resource1", "api://resource2"],
            M2mAccessTokenLifetimeSeconds = 7200
        };

        // Act
        var json = JsonSerializer.Serialize(client, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClientSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(2, deserialized.M2mAllowedAudiences?.Count);
        Assert.AreEqual(7200, deserialized.M2mAccessTokenLifetimeSeconds);
    }

    [TestMethod]
    public void ClientSeedDefinition_IncludesLogoutSettings()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "enterprise-app",
            ClientName = "Enterprise Application",
            BackChannelLogoutUri = "https://app.enterprise.com/backchannel-logout",
            BackChannelLogoutSessionRequired = true,
            FrontChannelLogoutUri = "https://app.enterprise.com/front-logout",
            FrontChannelLogoutSessionRequired = false
        };

        // Act
        var json = JsonSerializer.Serialize(client, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClientSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("https://app.enterprise.com/backchannel-logout", deserialized.BackChannelLogoutUri);
        Assert.IsTrue(deserialized.BackChannelLogoutSessionRequired ?? false);
        Assert.AreEqual("https://app.enterprise.com/front-logout", deserialized.FrontChannelLogoutUri);
        Assert.IsFalse(deserialized.FrontChannelLogoutSessionRequired ?? true);
    }

    [TestMethod]
    public void ClientExport_WithObfuscatedSecret_UsesMarker()
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
        Assert.AreEqual(ExportManifest.ObfuscatedMarker, deserialized.ClientSecretHash);
        Assert.IsTrue(ExportManifest.IsObfuscated(deserialized.ClientSecretHash));
    }

    [TestMethod]
    public void ClientExport_WithAllSecuritySettings_SerializesCorrectly()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "high-security",
            ClientName = "High Security Client",
            RequirePkce = true,
            RequirePar = true,
            RequireConsent = true
        };

        // Act
        var json = JsonSerializer.Serialize(client, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClientSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.IsTrue(deserialized.RequirePkce ?? false);
        Assert.IsTrue(deserialized.RequirePar ?? false);
        Assert.IsTrue(deserialized.RequireConsent ?? false);
    }

    [TestMethod]
    public void ClientExport_EmptyCollections_SerializesCorrectly()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "minimal-client",
            ClientName = "Minimal Client",
            AllowedScopes = [],
            AllowedLoginRedirectUris = [],
            IdentityProviderAssignments = []
        };

        // Act
        var json = JsonSerializer.Serialize(client, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClientSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("minimal-client", deserialized.ClientId);
        Assert.IsNotNull(deserialized.AllowedScopes);
        Assert.AreEqual(0, deserialized.AllowedScopes.Count);
        Assert.IsNotNull(deserialized.IdentityProviderAssignments);
        Assert.AreEqual(0, deserialized.IdentityProviderAssignments.Count);
    }
}
