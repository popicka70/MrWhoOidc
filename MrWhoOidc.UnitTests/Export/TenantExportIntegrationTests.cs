using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Export;

/// <summary>
/// Integration tests for the tenant export API endpoint.
/// </summary>
[TestClass]
public class TenantExportIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void ExportManifest_CanSerializeAndDeserialize_WithAllFields()
    {
        // Arrange
        var manifest = CreateTestManifest();

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(manifest.Version, deserialized.Version);
        Assert.AreEqual(manifest.Metadata?.ExportedAt, deserialized.Metadata?.ExportedAt);
        Assert.AreEqual(manifest.Data?.Tenants?.Count, deserialized.Data?.Tenants?.Count);
    }

    [TestMethod]
    public void ExportManifest_SerializesToCamelCase()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            Version = 1,
            ExportMode = "obfuscated",
            Metadata = new ExportMetadata
            {
                ExportedAt = DateTime.UtcNow,
                ExportedBy = "test-user",
                SourceSystem = "test-system"
            }
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        // Assert
        Assert.IsTrue(json.Contains("\"version\":"), "Expected camelCase version property");
        Assert.IsTrue(json.Contains("\"metadata\":"), "Expected camelCase metadata property");
        Assert.IsTrue(json.Contains("\"exportedAt\":"), "Expected camelCase exportedAt property");
        Assert.IsTrue(json.Contains("\"exportedBy\":"), "Expected camelCase exportedBy property");
    }

    [TestMethod]
    public void ExportManifest_IncludesExportMode_WhenSet()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            Version = 1,
            ExportMode = "full"
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("full", deserialized.ExportMode);
    }

    [TestMethod]
    public void TenantSeedDefinition_SerializesWithExtendedFields()
    {
        // Arrange
        var tenant = new TenantSeedDefinition
        {
            Slug = "test-tenant",
            Name = "Test Tenant",
            Description = "A test tenant",
            IssuerUri = "https://test.example.com",
            Status = "active",
            MaxUsers = 100,
            MaxClients = 50
        };

        // Act
        var json = JsonSerializer.Serialize(tenant, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TenantSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("test-tenant", deserialized.Slug);
        Assert.AreEqual("https://test.example.com", deserialized.IssuerUri);
        Assert.AreEqual("active", deserialized.Status);
        Assert.AreEqual(100, deserialized.MaxUsers);
        Assert.AreEqual(50, deserialized.MaxClients);
    }

    [TestMethod]
    public void ClientSeedDefinition_SerializesWithExtendedFields()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "test-client",
            ClientName = "Test Client",
            Realm = "admin",
            AllowedScopes = ["openid", "profile"],
            RequirePkce = true,
            AllowLocalLogin = true,
            AllowExternalIdp = true,
            IdTokenEncryptedResponseAlg = "RSA-OAEP",
            IdTokenEncryptedResponseEnc = "A256CBC-HS512",
            UserInfoSignedResponseAlg = "RS256",
            UserInfoEncryptedResponseAlg = "RSA-OAEP",
            UserInfoEncryptedResponseEnc = "A256CBC-HS512"
        };

        // Act
        var json = JsonSerializer.Serialize(client, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClientSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("test-client", deserialized.ClientId);
        Assert.AreEqual("admin", deserialized.Realm);
        Assert.AreEqual(true, deserialized.RequirePkce);
        Assert.AreEqual(2, deserialized.AllowedScopes.Count);
        Assert.AreEqual("RSA-OAEP", deserialized.IdTokenEncryptedResponseAlg);
        Assert.AreEqual("A256CBC-HS512", deserialized.IdTokenEncryptedResponseEnc);
        Assert.AreEqual("RS256", deserialized.UserInfoSignedResponseAlg);
        Assert.AreEqual("RSA-OAEP", deserialized.UserInfoEncryptedResponseAlg);
        Assert.AreEqual("A256CBC-HS512", deserialized.UserInfoEncryptedResponseEnc);
    }

    [TestMethod]
    public void IdentityProviderSeedDefinition_SerializesCorrectly()
    {
        // Arrange
        var provider = new IdentityProviderSeedDefinition
        {
            Name = "Azure AD",
            DisplayName = "Azure Active Directory",
            Type = "oidc",
            Enabled = true,
            IsDefault = false,
            Config = new Dictionary<string, object?>
            {
                ["authority"] = "https://login.microsoftonline.com/common/v2.0",
                ["clientId"] = "azure-client-id",
                ["clientSecret"] = "***OBFUSCATED***",
                ["scopes"] = new[] { "openid", "profile", "email" }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(provider, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<IdentityProviderSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("Azure AD", deserialized.Name);
        Assert.AreEqual("oidc", deserialized.Type);
        Assert.IsNotNull(deserialized.Config);
        Assert.IsTrue(deserialized.Config.ContainsKey("authority"));
        Assert.IsTrue(deserialized.Config.ContainsKey("clientSecret"));
    }

    [TestMethod]
    public void SeedManifest_SerializesTenantsCorrectly()
    {
        // Arrange
        var seedManifest = new SeedManifest
        {
            Version = 1,
            Tenants = new List<TenantSeedDefinition>
            {
                new()
                {
                    Slug = "tenant-1",
                    Name = "Tenant One"
                },
                new()
                {
                    Slug = "tenant-2",
                    Name = "Tenant Two"
                }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(seedManifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<SeedManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(2, deserialized.Tenants.Count);
        Assert.AreEqual("tenant-1", deserialized.Tenants[0].Slug);
        Assert.AreEqual("tenant-2", deserialized.Tenants[1].Slug);
    }

    [TestMethod]
    public void ObfuscatedMarker_IsCorrect()
    {
        // Assert
        Assert.AreEqual("***OBFUSCATED***", ExportManifest.ObfuscatedMarker);
    }

    [TestMethod]
    public void IsObfuscated_ReturnsTrue_ForObfuscatedValue()
    {
        // Arrange
        var obfuscatedValue = ExportManifest.ObfuscatedMarker;

        // Act
        var result = ExportManifest.IsObfuscated(obfuscatedValue);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void IsObfuscated_ReturnsFalse_ForNonObfuscatedValue()
    {
        // Arrange
        var plainValue = "my-secret-value";

        // Act
        var result = ExportManifest.IsObfuscated(plainValue);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ObfuscateSecret_ReturnsMarker_ForNonEmptyValue()
    {
        // Arrange
        var secretValue = "my-secret-value";

        // Act
        var result = ExportManifest.ObfuscateSecret(secretValue);

        // Assert
        Assert.AreEqual(ExportManifest.ObfuscatedMarker, result);
    }

    [TestMethod]
    public void ObfuscateSecret_ReturnsNull_ForEmptyValue()
    {
        // Arrange
        var emptyValue = "";

        // Act
        var result = ExportManifest.ObfuscateSecret(emptyValue);

        // Assert
        Assert.IsNull(result);
    }

    private static ExportManifest CreateTestManifest()
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
                Tenants = new List<TenantSeedDefinition>
                {
                    new()
                    {
                        Slug = "test-tenant",
                        Name = "Test Tenant",
                        IssuerUri = "https://test.example.com",
                        Status = "active",
                        Realms = new List<RealmSeedDefinition>
                        {
                            new()
                            {
                                Name = "admin",
                                DisplayName = "Admin Realm"
                            }
                        },
                        Clients = new List<ClientSeedDefinition>
                        {
                            new()
                            {
                                ClientId = "test-client",
                                ClientName = "Test Client",
                                Realm = "admin"
                            }
                        }
                    }
                }
            }
        };
    }
}
