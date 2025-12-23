namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Specifies how secrets should be handled during export.
/// </summary>
public enum ExportMode
{
    /// <summary>
    /// Secrets are replaced with a placeholder marker (***OBFUSCATED***).
    /// This is the default and recommended mode for sharing configurations.
    /// </summary>
    Obfuscated = 0,

    /// <summary>
    /// Hashed secret values are included in the export.
    /// Use only when migrating between trusted environments.
    /// Note: Plaintext secrets are NEVER exported - only their hashed forms.
    /// </summary>
    Full = 1
}

/// <summary>
/// Specifies how conflicts should be resolved during import.
/// </summary>
public enum ConflictResolution
{
    /// <summary>
    /// Skip the conflicting entity and continue with the rest of the import.
    /// </summary>
    Skip = 0,

    /// <summary>
    /// Auto-rename the entity (e.g., append "-imported") and import as new.
    /// </summary>
    Rename = 1,

    /// <summary>
    /// Merge with the existing entity, updating only non-conflicting fields.
    /// Preserves existing values where the import doesn't specify changes.
    /// </summary>
    Merge = 2,

    /// <summary>
    /// Replace the existing entity entirely with the imported configuration.
    /// Warning: This will overwrite all existing settings.
    /// </summary>
    Overwrite = 3
}

/// <summary>
/// Types of conflicts that can occur during import.
/// </summary>
public enum ConflictType
{
    /// <summary>
    /// A tenant with the same slug already exists.
    /// </summary>
    TenantSlugExists = 0,

    /// <summary>
    /// A realm with the same name already exists in the tenant.
    /// </summary>
    RealmNameExists = 1,

    /// <summary>
    /// A client with the same client_id already exists in the tenant.
    /// </summary>
    ClientIdExists = 2,

    /// <summary>
    /// An identity provider with the same name already exists in the tenant.
    /// </summary>
    ProviderNameExists = 3,

    /// <summary>
    /// A scope with the same name conflicts with an existing scope.
    /// </summary>
    ScopeNameConflict = 4,

    /// <summary>
    /// A role with the same name already exists in the realm.
    /// </summary>
    RoleNameExists = 5
}

/// <summary>
/// Severity level for validation issues during import.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>
    /// An error that prevents the import from proceeding.
    /// </summary>
    Error = 0,

    /// <summary>
    /// A warning that should be reviewed but doesn't block the import.
    /// </summary>
    Warning = 1
}
