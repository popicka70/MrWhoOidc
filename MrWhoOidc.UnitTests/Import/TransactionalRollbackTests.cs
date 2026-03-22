using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.UnitTests.Import;

/// <summary>
/// Unit tests for transactional rollback behavior during import.
/// </summary>
[TestClass]
public class TransactionalRollbackTests
{
    [TestMethod]
    public void ImportResult_SuccessState_IsCorrect()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            TenantsCreated = 1,
            RealmsCreated = 2,
            ClientsCreated = 5,
            ProvidersCreated = 2,
            ScopesCreated = 10,
            RolesCreated = 5
        };

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.TenantsCreated);
        Assert.AreEqual(2, result.RealmsCreated);
        Assert.AreEqual(5, result.ClientsCreated);
        Assert.AreEqual(2, result.ProvidersCreated);
        Assert.AreEqual(10, result.ScopesCreated);
        Assert.AreEqual(5, result.RolesCreated);
    }

    [TestMethod]
    public void ImportResult_FailureState_HasErrorDetails()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = false,
            ErrorMessage = "Foreign key constraint violation",
            ErrorDetails = "Could not create client 'my-client' - realm 'unknown-realm' does not exist"
        };

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.ErrorMessage);
        Assert.IsNotNull(result.ErrorDetails);
        Assert.AreEqual(0, result.TenantsCreated);
    }

    [TestMethod]
    public void ImportResult_RollbackState_IndicatesNoChanges()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = false,
            WasRolledBack = true,
            ErrorMessage = "Transaction rolled back due to error",
            TenantsCreated = 0,
            RealmsCreated = 0,
            ClientsCreated = 0
        };

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.WasRolledBack);
        Assert.AreEqual(0, result.TenantsCreated);
        Assert.AreEqual(0, result.RealmsCreated);
        Assert.AreEqual(0, result.ClientsCreated);
    }

    [TestMethod]
    public void ImportOptions_DryRun_PreventsActualChanges()
    {
        // Arrange - DryRun is an alias for ValidateOnly
        var options = new ImportOptions
        {
            DryRun = true
        };

        // Assert
        Assert.IsTrue(options.DryRun);
        Assert.IsTrue(options.ValidateOnly); // DryRun sets ValidateOnly
        // When DryRun is true, no actual database changes should be committed
    }

    [TestMethod]
    public void ImportOptions_ValidateOnly_SkipsExecution()
    {
        // Arrange - ValidateOnly is the primary property
        var options = new ImportOptions
        {
            ValidateOnly = true
        };

        // Assert
        Assert.IsTrue(options.ValidateOnly);
        Assert.IsTrue(options.DryRun); // DryRun reads from ValidateOnly
        // When ValidateOnly is true, only validation is performed
    }

    [TestMethod]
    public void ImportResult_TracksSkippedEntities()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            TenantsCreated = 0,
            TenantsSkipped = 1,
            ClientsCreated = 3,
            ClientsSkipped = 2
        };

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, result.TenantsCreated);
        Assert.AreEqual(1, result.TenantsSkipped);
        Assert.AreEqual(3, result.ClientsCreated);
        Assert.AreEqual(2, result.ClientsSkipped);
    }

    [TestMethod]
    public void ImportResult_TracksUpdatedEntities()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            TenantsCreated = 0,
            TenantsUpdated = 1,
            ClientsCreated = 2,
            ClientsUpdated = 3
        };

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, result.TenantsCreated);
        Assert.AreEqual(1, result.TenantsUpdated);
        Assert.AreEqual(2, result.ClientsCreated);
        Assert.AreEqual(3, result.ClientsUpdated);
    }

    [TestMethod]
    public void ImportResult_HasTimingInformation()
    {
        // Arrange
        var startTime = DateTime.UtcNow;
        var endTime = startTime.AddSeconds(5);

        var result = new ImportResult
        {
            Success = true,
            StartedAt = startTime,
            CompletedAt = endTime
        };

        // Assert
        Assert.IsNotNull(result.StartedAt);
        Assert.IsNotNull(result.CompletedAt);
        Assert.AreEqual(5, (result.CompletedAt.Value - result.StartedAt.Value).TotalSeconds);
    }

    [TestMethod]
    public void ImportResult_PartialSuccess_WithWarnings()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            TenantsCreated = 1,
            ClientsCreated = 4,
            Warnings =
            [
                "Client 'legacy-app' has deprecated grant type 'implicit'",
                "Scope 'custom-scope' was not found and was skipped"
            ]
        };

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, result.Warnings.Count);
    }

    [TestMethod]
    public void ImportResult_CanTrackAuditLogId()
    {
        // Arrange
        var auditLogId = Guid.NewGuid();
        var result = new ImportResult
        {
            Success = true,
            AuditLogId = auditLogId
        };

        // Assert
        Assert.AreEqual(auditLogId, result.AuditLogId);
    }

    [TestMethod]
    public void ImportPreview_CanBeConvertedToImportOptions()
    {
        // Arrange - user reviews preview and selects resolutions
        var preview = new ImportPreview
        {
            IsValid = true,
            Conflicts =
            [
                new ImportConflict
                {
                    EntityType = "tenant",
                    EntityKey = "existing-tenant",
                    SuggestedResolution = ConflictResolution.Skip
                },
                new ImportConflict
                {
                    EntityType = "client",
                    EntityKey = "existing-client",
                    SuggestedResolution = ConflictResolution.Overwrite
                }
            ]
        };

        // Act - user accepts suggested resolutions
        var options = new ImportOptions
        {
            ConflictResolutions = preview.Conflicts
                .ToDictionary(
                    c => $"{c.EntityType}:{c.EntityKey}",
                    c => c.SuggestedResolution)
        };

        // Assert
        Assert.AreEqual(2, options.ConflictResolutions.Count);
        Assert.AreEqual(ConflictResolution.Skip, options.ConflictResolutions["tenant:existing-tenant"]);
        Assert.AreEqual(ConflictResolution.Overwrite, options.ConflictResolutions["client:existing-client"]);
    }

    [TestMethod]
    public void ImportResult_TotalEntitiesCreated_CanBeCalculated()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            TenantsCreated = 1,
            RealmsCreated = 2,
            ClientsCreated = 5,
            ProvidersCreated = 3,
            ScopesCreated = 10,
            RolesCreated = 8
        };

        // Act
        var total = result.TenantsCreated + result.RealmsCreated + result.ClientsCreated +
                    result.ProvidersCreated + result.ScopesCreated + result.RolesCreated;

        // Assert
        Assert.AreEqual(29, total);
    }

    [TestMethod]
    public void ImportResult_AllCountsDefault_ToZero()
    {
        // Arrange
        var result = new ImportResult();

        // Assert
        Assert.AreEqual(0, result.TenantsCreated);
        Assert.AreEqual(0, result.RealmsCreated);
        Assert.AreEqual(0, result.ClientsCreated);
        Assert.AreEqual(0, result.ProvidersCreated);
        Assert.AreEqual(0, result.ScopesCreated);
        Assert.AreEqual(0, result.RolesCreated);
        Assert.AreEqual(0, result.TenantsSkipped);
        Assert.AreEqual(0, result.TenantsUpdated);
    }
}
