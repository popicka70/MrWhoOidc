namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Preview information for an import operation before execution.
/// </summary>
public sealed record ImportPreview
{
    /// <summary>
    /// Whether the manifest is valid and can be imported.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Validation errors that prevent import (if any).
    /// </summary>
    public List<ValidationError> ValidationErrors { get; init; } = [];

    /// <summary>
    /// Detected conflicts with existing entities.
    /// </summary>
    public List<ImportConflict> Conflicts { get; init; } = [];

    /// <summary>
    /// Entities that will be created during import.
    /// </summary>
    public List<EntitySummary> EntitiesToCreate { get; init; } = [];

    /// <summary>
    /// Entities that will be updated during import.
    /// </summary>
    public List<EntitySummary> EntitiesToUpdate { get; init; } = [];

    /// <summary>
    /// Non-blocking warnings about the import.
    /// </summary>
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// Result of an import operation.
/// </summary>
public sealed record ImportResult
{
    /// <summary>
    /// Whether the import completed successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Number of entities created.
    /// </summary>
    public int EntitiesCreated { get; init; }

    /// <summary>
    /// Number of entities updated.
    /// </summary>
    public int EntitiesUpdated { get; init; }

    /// <summary>
    /// Number of entities skipped (due to conflict resolution).
    /// </summary>
    public int EntitiesSkipped { get; init; }

    /// <summary>
    /// Errors that occurred during import.
    /// </summary>
    public List<ImportError> Errors { get; init; } = [];

    /// <summary>
    /// Non-blocking warnings from the import.
    /// </summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>
    /// ID of the audit log entry for this operation.
    /// </summary>
    public Guid? AuditLogId { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static ImportResult Successful(int created, int updated, int skipped, Guid? auditLogId = null) => new()
    {
        Success = true,
        EntitiesCreated = created,
        EntitiesUpdated = updated,
        EntitiesSkipped = skipped,
        AuditLogId = auditLogId
    };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static ImportResult Failed(params ImportError[] errors) => new()
    {
        Success = false,
        Errors = [.. errors]
    };
}

/// <summary>
/// A conflict detected during import preview.
/// </summary>
public sealed record ImportConflict
{
    /// <summary>
    /// Type of conflict.
    /// </summary>
    public ConflictType Type { get; init; }

    /// <summary>
    /// Type of entity: "Tenant", "Realm", "Client", "Provider".
    /// </summary>
    public string EntityType { get; init; } = string.Empty;

    /// <summary>
    /// Identifier of the conflicting entity (slug, clientId, name).
    /// </summary>
    public string Identifier { get; init; } = string.Empty;

    /// <summary>
    /// ID of the existing entity (if known).
    /// </summary>
    public Guid? ExistingEntityId { get; init; }

    /// <summary>
    /// Suggested new name for rename resolution.
    /// </summary>
    public string? SuggestedRename { get; init; }

    /// <summary>
    /// User's resolution choice (set during import execution).
    /// </summary>
    public ConflictResolution? Resolution { get; init; }
}

/// <summary>
/// A validation error found in the manifest.
/// </summary>
public sealed record ValidationError
{
    /// <summary>
    /// JSON path to the problematic field (e.g., "tenants[0].clients[2].clientId").
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Error code for programmatic handling.
    /// </summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Severity of the error.
    /// </summary>
    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Error;
}

/// <summary>
/// An error that occurred during import execution.
/// </summary>
public sealed record ImportError
{
    /// <summary>
    /// Type of entity that failed.
    /// </summary>
    public string EntityType { get; init; } = string.Empty;

    /// <summary>
    /// Identifier of the entity that failed.
    /// </summary>
    public string Identifier { get; init; } = string.Empty;

    /// <summary>
    /// Error code for programmatic handling.
    /// </summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Summary of an entity for preview purposes.
/// </summary>
public sealed record EntitySummary
{
    /// <summary>
    /// Type of entity: "Tenant", "Realm", "Client", "Provider", "Role", "Scope".
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Identifier of the entity (slug, clientId, name).
    /// </summary>
    public string Identifier { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable display name (if available).
    /// </summary>
    public string? DisplayName { get; init; }
}
