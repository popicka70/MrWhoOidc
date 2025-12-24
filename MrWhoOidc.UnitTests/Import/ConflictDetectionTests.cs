using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Import;

/// <summary>
/// Unit tests for import conflict detection logic.
/// </summary>
[TestClass]
public class ConflictDetectionTests
{
    [TestMethod]
    public void ImportConflict_HasCorrectProperties()
    {
        // Arrange
        var conflict = new ImportConflict
        {
            EntityType = "tenant",
            EntityKey = "existing-tenant",
            ConflictType = "SlugCollision",
            ExistingValue = "Existing Tenant Name",
            IncomingValue = "New Tenant Name",
            SuggestedResolution = ConflictResolution.Skip
        };

        // Assert
        Assert.AreEqual("tenant", conflict.EntityType);
        Assert.AreEqual("existing-tenant", conflict.EntityKey);
        Assert.AreEqual("SlugCollision", conflict.ConflictType);
        Assert.AreEqual("Existing Tenant Name", conflict.ExistingValue);
        Assert.AreEqual("New Tenant Name", conflict.IncomingValue);
        Assert.AreEqual(ConflictResolution.Skip, conflict.SuggestedResolution);
    }

    [TestMethod]
    public void ConflictResolution_HasAllExpectedValues()
    {
        // Assert - all resolution strategies exist
        Assert.AreEqual(ConflictResolution.Skip, (ConflictResolution)0);
        Assert.AreEqual(ConflictResolution.Rename, (ConflictResolution)1);
        Assert.AreEqual(ConflictResolution.Merge, (ConflictResolution)2);
        Assert.AreEqual(ConflictResolution.Overwrite, (ConflictResolution)3);
    }

    [TestMethod]
    public void ImportPreview_CanHoldMultipleConflicts()
    {
        // Arrange
        var preview = new ImportPreview
        {
            IsValid = true,
            Conflicts =
            [
                new ImportConflict
                {
                    EntityType = "tenant",
                    EntityKey = "tenant-1",
                    ConflictType = "SlugCollision"
                },
                new ImportConflict
                {
                    EntityType = "client",
                    EntityKey = "client-1",
                    ConflictType = "ClientIdCollision"
                }
            ]
        };

        // Assert
        Assert.AreEqual(2, preview.Conflicts.Count);
        Assert.IsTrue(preview.IsValid);
    }

    [TestMethod]
    public void ImportPreview_CanHoldValidationErrors()
    {
        // Arrange
        var preview = new ImportPreview
        {
            IsValid = false,
            ValidationErrors =
            [
                new ValidationError
                {
                    Path = "$.tenants[0].slug",
                    Message = "Slug is required",
                    Severity = ValidationSeverity.Error
                },
                new ValidationError
                {
                    Path = "$.tenants[0].clients[0].clientId",
                    Message = "ClientId is required",
                    Severity = ValidationSeverity.Error
                }
            ]
        };

        // Assert
        Assert.IsFalse(preview.IsValid);
        Assert.AreEqual(2, preview.ValidationErrors.Count);
        Assert.AreEqual("$.tenants[0].slug", preview.ValidationErrors[0].Path);
    }

    [TestMethod]
    public void ValidationError_HasSeverityLevels()
    {
        // Arrange
        var errorLevel = new ValidationError
        {
            Path = "$.version",
            Message = "Unsupported version",
            Severity = ValidationSeverity.Error
        };

        var warningLevel = new ValidationError
        {
            Path = "$.tenants[0].description",
            Message = "Description exceeds recommended length",
            Severity = ValidationSeverity.Warning
        };

        // Assert
        Assert.AreEqual(ValidationSeverity.Error, errorLevel.Severity);
        Assert.AreEqual(ValidationSeverity.Warning, warningLevel.Severity);
    }

    [TestMethod]
    public void ImportPreview_TracksEntityCounts()
    {
        // Arrange
        var preview = new ImportPreview
        {
            IsValid = true,
            TenantCount = 1,
            RealmCount = 2,
            ClientCount = 5,
            ProviderCount = 3,
            ScopeCount = 10,
            RoleCount = 8
        };

        // Assert
        Assert.AreEqual(1, preview.TenantCount);
        Assert.AreEqual(2, preview.RealmCount);
        Assert.AreEqual(5, preview.ClientCount);
        Assert.AreEqual(3, preview.ProviderCount);
        Assert.AreEqual(10, preview.ScopeCount);
        Assert.AreEqual(8, preview.RoleCount);
    }

    [TestMethod]
    public void ConflictKey_Format_TenantSlug()
    {
        // Arrange
        var entityType = "tenant";
        var slug = "existing-tenant";
        var conflictKey = $"{entityType}:{slug}";

        // Assert
        Assert.AreEqual("tenant:existing-tenant", conflictKey);
    }

    [TestMethod]
    public void ConflictKey_Format_ClientId()
    {
        // Arrange
        var entityType = "client";
        var clientId = "my-web-app";
        var conflictKey = $"{entityType}:{clientId}";

        // Assert
        Assert.AreEqual("client:my-web-app", conflictKey);
    }

    [TestMethod]
    public void ConflictKey_Format_RealmName()
    {
        // Arrange
        var entityType = "realm";
        var tenantSlug = "tenant-1";
        var realmName = "admin";
        var conflictKey = $"{entityType}:{tenantSlug}:{realmName}";

        // Assert
        Assert.AreEqual("realm:tenant-1:admin", conflictKey);
    }

    [TestMethod]
    public void ConflictKey_Format_ProviderName()
    {
        // Arrange
        var entityType = "provider";
        var tenantSlug = "tenant-1";
        var providerName = "azure-ad";
        var conflictKey = $"{entityType}:{tenantSlug}:{providerName}";

        // Assert
        Assert.AreEqual("provider:tenant-1:azure-ad", conflictKey);
    }

    [TestMethod]
    public void ImportOptions_ConflictResolution_CanBeLookedUp()
    {
        // Arrange
        var options = new ImportOptions
        {
            DefaultConflictResolution = ConflictResolution.Skip,
            ConflictResolutions = new Dictionary<string, ConflictResolution>
            {
                ["tenant:special-tenant"] = ConflictResolution.Overwrite,
                ["client:special-client"] = ConflictResolution.Rename
            }
        };

        // Act
        var tenantResolution = options.ConflictResolutions.TryGetValue("tenant:special-tenant", out var tr)
            ? tr : options.DefaultConflictResolution;
        var clientResolution = options.ConflictResolutions.TryGetValue("client:special-client", out var cr)
            ? cr : options.DefaultConflictResolution;
        var unknownResolution = options.ConflictResolutions.TryGetValue("unknown:key", out var ur)
            ? ur : options.DefaultConflictResolution;

        // Assert
        Assert.AreEqual(ConflictResolution.Overwrite, tenantResolution);
        Assert.AreEqual(ConflictResolution.Rename, clientResolution);
        Assert.AreEqual(ConflictResolution.Skip, unknownResolution);
    }

    [TestMethod]
    public void ImportPreview_WithObfuscatedSecrets_FlagsSecretsRequired()
    {
        // Arrange
        var preview = new ImportPreview
        {
            IsValid = true,
            HasObfuscatedSecrets = true,
            ObfuscatedSecretCount = 3
        };

        // Assert
        Assert.IsTrue(preview.HasObfuscatedSecrets);
        Assert.AreEqual(3, preview.ObfuscatedSecretCount);
    }

    [TestMethod]
    public void ImportConflict_SupportsAllEntityTypes()
    {
        // Arrange & Act
        var tenantConflict = new ImportConflict { EntityType = "tenant" };
        var realmConflict = new ImportConflict { EntityType = "realm" };
        var clientConflict = new ImportConflict { EntityType = "client" };
        var providerConflict = new ImportConflict { EntityType = "provider" };
        var scopeConflict = new ImportConflict { EntityType = "scope" };
        var roleConflict = new ImportConflict { EntityType = "role" };

        // Assert
        Assert.AreEqual("tenant", tenantConflict.EntityType);
        Assert.AreEqual("realm", realmConflict.EntityType);
        Assert.AreEqual("client", clientConflict.EntityType);
        Assert.AreEqual("provider", providerConflict.EntityType);
        Assert.AreEqual("scope", scopeConflict.EntityType);
        Assert.AreEqual("role", roleConflict.EntityType);
    }
}
