using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Export;

/// <summary>
/// Tests for tenant export serialization to JSON format.
/// </summary>
[TestClass]
public class TenantExportSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void ExportManifest_SerializesToJson_WithCorrectSchema()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            ExportType = "tenant",
            ExportMode = "obfuscated",
            Metadata = new ExportMetadata
            {
                ExportedAt = DateTimeOffset.Parse("2025-12-23T10:00:00Z"),
                ExportedBy = "admin@test.com",
                SourceSystem = "test-server",
                SourceTenant = "acme"
            },
            Data = new SeedManifest
            {
                Version = 1,
                Tenants =
                [
                    new TenantSeedDefinition
                    {
                        Slug = "acme",
                        Name = "ACME Corporation"
                    }
                ]
            }
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        // Assert
        Assert.IsNotNull(json);
        Assert.IsTrue(json.Contains("\"$schema\""));
        Assert.IsTrue(json.Contains("\"version\":"));
        Assert.IsTrue(json.Contains("\"exportType\": \"tenant\""));
        Assert.IsTrue(json.Contains("\"exportMode\": \"obfuscated\""));
    }

    [TestMethod]
    public void ExportManifest_DeserializesFromJson_Correctly()
    {
        // Arrange
        var json = """
        {
            "$schema": "https://mrwhooidc.io/schemas/export/v1",
            "version": 1,
            "exportType": "tenant",
            "exportMode": "obfuscated",
            "metadata": {
                "exportedAt": "2025-12-23T10:00:00Z",
                "exportedBy": "admin@test.com",
                "sourceTenant": "acme"
            },
            "data": {
                "version": 1,
                "tenants": [
                    {
                        "slug": "acme",
                        "name": "ACME Corporation"
                    }
                ]
            }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(manifest);
        Assert.AreEqual("tenant", manifest.ExportType);
        Assert.AreEqual("obfuscated", manifest.ExportMode);
        Assert.IsNotNull(manifest.Metadata);
        Assert.AreEqual("admin@test.com", manifest.Metadata.ExportedBy);
        Assert.AreEqual("acme", manifest.Metadata.SourceTenant);
        Assert.IsNotNull(manifest.Data);
        Assert.AreEqual(1, manifest.Data.Tenants?.Count);
        Assert.AreEqual("acme", manifest.Data.Tenants?[0].Slug);
    }

    [TestMethod]
    public void TenantSeedDefinition_IncludesAllExtendedFields()
    {
        // Arrange
        var tenant = new TenantSeedDefinition
        {
            Slug = "test-tenant",
            Name = "Test Tenant",
            Description = "A test tenant",
            IssuerUri = "https://auth.test.com",
            AdminEmail = "admin@test.com",
            BillingPlan = "enterprise",
            Status = "Active",
            LogoUrl = "https://test.com/logo.png",
            PrimaryColor = "#007bff",
            AccentColor = "#28a745",
            MaxUsers = 1000,
            MaxClients = 100,
            Realms =
            [
                new RealmSeedDefinition { Name = "admin", DisplayName = "Admin Realm" }
            ],
            Clients =
            [
                new ClientSeedDefinition
                {
                    ClientId = "web-app",
                    ClientName = "Web Application",
                    RequirePkce = true,
                    RequireConsent = false
                }
            ],
            IdentityProviders =
            [
                new IdentityProviderSeedDefinition
                {
                    Name = "azure-ad",
                    DisplayName = "Azure AD",
                    Type = "oidc",
                    Enabled = true
                }
            ],
            Roles =
            [
                new RoleSeedDefinition
                {
                    Name = "admin",
                    RealmName = "admin",
                    IsActive = true
                }
            ]
        };

        // Act
        var json = JsonSerializer.Serialize(tenant, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TenantSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("test-tenant", deserialized.Slug);
        Assert.AreEqual("Test Tenant", deserialized.Name);
        Assert.AreEqual("#007bff", deserialized.PrimaryColor);
        Assert.AreEqual(1000, deserialized.MaxUsers);
        Assert.AreEqual(1, deserialized.Realms?.Count);
        Assert.AreEqual(1, deserialized.Clients?.Count);
        Assert.AreEqual(1, deserialized.IdentityProviders?.Count);
        Assert.AreEqual(1, deserialized.Roles?.Count);
    }

    [TestMethod]
    public void ClientSeedDefinition_IncludesOboAndM2mSettings()
    {
        // Arrange
        var client = new ClientSeedDefinition
        {
            ClientId = "api-client",
            ClientName = "API Client",
            RequirePar = true,
            OboEnabled = true,
            OboMaxDelegationDepth = 3,
            OboMaxLifetimeMinutes = 60,
            OboDpopMode = "Required",
            OboAllowedCallers = ["caller-1", "caller-2"],
            OboAllowedSourceAudiences = ["aud-1"],
            OboAllowedTargetAudiences = ["aud-2"],
            OboAllowedScopes = ["api.read", "api.write"],
            M2mAllowedAudiences = ["api://resource"],
            M2mAccessTokenLifetimeSeconds = 3600,
            BackChannelLogoutUri = "https://app.test.com/logout",
            BackChannelLogoutSessionRequired = true,
            FrontChannelLogoutUri = "https://app.test.com/fc-logout",
            FrontChannelLogoutSessionRequired = false
        };

        // Act
        var json = JsonSerializer.Serialize(client, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClientSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(true, deserialized.OboEnabled);
        Assert.AreEqual(3, deserialized.OboMaxDelegationDepth);
        Assert.AreEqual(60, deserialized.OboMaxLifetimeMinutes);
        Assert.AreEqual("Required", deserialized.OboDpopMode);
        Assert.AreEqual(2, deserialized.OboAllowedCallers?.Count);
        Assert.AreEqual(3600, deserialized.M2mAccessTokenLifetimeSeconds);
        Assert.AreEqual("https://app.test.com/logout", deserialized.BackChannelLogoutUri);
    }

    [TestMethod]
    public void IdentityProviderSeedDefinition_IncludesClaimMappingsAndKeys()
    {
        // Arrange
        var provider = new IdentityProviderSeedDefinition
        {
            Name = "azure-ad",
            DisplayName = "Azure Active Directory",
            Type = "oidc",
            Enabled = true,
            IsDefault = true,
            LogoUrl = "https://azure.com/logo.png",
            SortOrder = 1,
            Config = new Dictionary<string, object?>
            {
                ["authority"] = "https://login.microsoftonline.com/tenant",
                ["clientId"] = "client-123",
                ["clientSecret"] = "***OBFUSCATED***"
            },
            ClaimMappings =
            [
                new ClaimMappingSeedDefinition
                {
                    ExternalClaim = "preferred_username",
                    LocalClaim = "email",
                    Transform = "lowercase",
                    Order = 1
                }
            ],
            Keys =
            [
                new ProviderKeySeedDefinition
                {
                    Purpose = "signing",
                    Alg = "RS256",
                    Kid = "key-1",
                    Active = true
                }
            ]
        };

        // Act
        var json = JsonSerializer.Serialize(provider, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<IdentityProviderSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("azure-ad", deserialized.Name);
        Assert.AreEqual("oidc", deserialized.Type);
        Assert.IsTrue(deserialized.IsDefault);
        Assert.IsNotNull(deserialized.Config);
        Assert.AreEqual(3, deserialized.Config.Count);
        Assert.AreEqual(1, deserialized.ClaimMappings?.Count);
        Assert.AreEqual("lowercase", deserialized.ClaimMappings?[0].Transform);
        Assert.AreEqual(1, deserialized.Keys?.Count);
        Assert.AreEqual("RS256", deserialized.Keys?[0].Alg);
    }

    [TestMethod]
    public void ClientIdpAssignmentSeedDefinition_SerializesCorrectly()
    {
        // Arrange
        var assignment = new ClientIdpAssignmentSeedDefinition
        {
            ProviderName = "azure-ad",
            Enabled = true,
            IsDefaultForClient = true,
            AutoRedirectIfSingle = true,
            RequiredAcr = "urn:mfa",
            Order = 1
        };

        // Act
        var json = JsonSerializer.Serialize(assignment, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ClientIdpAssignmentSeedDefinition>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("azure-ad", deserialized.ProviderName);
        Assert.IsTrue(deserialized.Enabled);
        Assert.IsTrue(deserialized.IsDefaultForClient);
        Assert.AreEqual("urn:mfa", deserialized.RequiredAcr);
    }

    [TestMethod]
    public void ExportMetadata_IncludesChecksumWhenProvided()
    {
        // Arrange
        var metadata = new ExportMetadata
        {
            ExportedAt = DateTimeOffset.UtcNow,
            ExportedBy = "admin",
            SourceSystem = "test",
            SourceVersion = "1.0.0",
            SourceTenant = "acme",
            Checksum = "sha256:abc123def456"
        };

        // Act
        var json = JsonSerializer.Serialize(metadata, JsonOptions);

        // Assert
        Assert.IsTrue(json.Contains("\"checksum\": \"sha256:abc123def456\""));
    }

    [TestMethod]
    public void SeedManifest_RoundTripsWithCompleteStructure()
    {
        // Arrange
        var original = new SeedManifest
        {
            Version = 1,
            Scopes =
            [
                new ScopeSeedDefinition
                {
                    Name = "api.read",
                    Description = "Read API access",
                    IsExposed = true,
                    TenantSlug = "acme"
                }
            ],
            Tenants =
            [
                new TenantSeedDefinition
                {
                    Slug = "acme",
                    Name = "ACME Corp",
                    Realms =
                    [
                        new RealmSeedDefinition
                        {
                            Name = "default",
                            DisplayName = "Default Realm",
                            AllowUnconfirmedLogin = false
                        }
                    ]
                }
            ]
        };

        // Act
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<SeedManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(1, deserialized.Version);
        Assert.AreEqual(1, deserialized.Scopes?.Count);
        Assert.AreEqual("api.read", deserialized.Scopes?[0].Name);
        Assert.AreEqual(1, deserialized.Tenants?.Count);
        Assert.AreEqual("acme", deserialized.Tenants?[0].Slug);
        Assert.AreEqual(1, deserialized.Tenants?[0].Realms?.Count);
    }
}
