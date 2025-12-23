namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Options for configuring how configuration imports are processed.
/// </summary>
public sealed record ImportOptions
{
    /// <summary>
    /// Default conflict resolution strategy when a specific override is not provided.
    /// Default is Skip (do not import conflicting entities).
    /// </summary>
    public ConflictResolution DefaultConflictResolution { get; init; } = ConflictResolution.Skip;

    /// <summary>
    /// Per-entity conflict resolution overrides.
    /// Key format: "{EntityType}:{Identifier}" (e.g., "Client:web-app", "Realm:admin").
    /// </summary>
    public Dictionary<string, ConflictResolution> ConflictOverrides { get; init; } = [];

    /// <summary>
    /// Whether to only validate without actually applying changes.
    /// When true, performs all validation and conflict detection but does not modify data.
    /// </summary>
    public bool ValidateOnly { get; init; }

    /// <summary>
    /// Secrets to use for entities with obfuscated values.
    /// Key format for clients: "{ClientId}" → secret value.
    /// Key format for providers: "{ProviderName}" → secret value.
    /// </summary>
    public Dictionary<string, string> Secrets { get; init; } = [];

    /// <summary>
    /// Optional target tenant ID when importing realm/client/provider into an existing tenant.
    /// When specified, overrides the tenant information in the import file.
    /// </summary>
    public Guid? TargetTenantId { get; init; }

    /// <summary>
    /// Optional target realm ID when importing a client into a specific realm.
    /// </summary>
    public Guid? TargetRealmId { get; init; }

    /// <summary>
    /// Optional username of the admin performing the import.
    /// Used for audit logging.
    /// </summary>
    public string? ImportedBy { get; init; }

    /// <summary>
    /// Optional IP address of the request.
    /// Used for audit logging.
    /// </summary>
    public string? IpAddress { get; init; }

    /// <summary>
    /// Optional User-Agent header.
    /// Used for audit logging.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Default import options (validate and apply with Skip conflict resolution).
    /// </summary>
    public static ImportOptions Default => new();

    /// <summary>
    /// Import options for preview-only mode (validation without changes).
    /// </summary>
    public static ImportOptions PreviewOnly => new() { ValidateOnly = true };

    /// <summary>
    /// Gets the conflict resolution for a specific entity.
    /// </summary>
    /// <param name="entityType">The type of entity (e.g., "Tenant", "Client").</param>
    /// <param name="identifier">The identifier of the entity (e.g., slug, clientId).</param>
    /// <returns>The resolution to apply for the entity.</returns>
    public ConflictResolution GetResolution(string entityType, string identifier)
    {
        var key = $"{entityType}:{identifier}";
        return ConflictOverrides.TryGetValue(key, out var resolution)
            ? resolution
            : DefaultConflictResolution;
    }
}
