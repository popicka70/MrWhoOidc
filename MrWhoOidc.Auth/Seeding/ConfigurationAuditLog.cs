using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Audit log entry for configuration export/import operations.
/// Tracks when administrators export or import OIDC configurations for security and compliance.
/// </summary>
public class ConfigurationAuditLog
{
    /// <summary>
    /// Unique identifier for this audit log entry.
    /// </summary>
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// The tenant affected by the operation. Null for platform-level operations.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Type of operation: "Export" or "Import".
    /// </summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Type of entity: "Tenant", "Realm", "Client", or "Provider".
    /// </summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the entity (slug, clientId, name).
    /// </summary>
    public string? EntityIdentifier { get; set; }

    /// <summary>
    /// Export mode used: "Obfuscated" or "Full".
    /// </summary>
    public string ExportMode { get; set; } = string.Empty;

    /// <summary>
    /// Result of the operation: "Success", "Failed", or "PartialSuccess".
    /// </summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>
    /// Number of entities created (import only).
    /// </summary>
    public int? EntitiesCreated { get; set; }

    /// <summary>
    /// Number of entities updated (import only).
    /// </summary>
    public int? EntitiesUpdated { get; set; }

    /// <summary>
    /// Number of entities skipped (import only).
    /// </summary>
    public int? EntitiesSkipped { get; set; }

    /// <summary>
    /// Error details if the operation failed.
    /// </summary>
    public string? ErrorDetails { get; set; }

    /// <summary>
    /// SHA-256 checksum of the manifest data.
    /// </summary>
    public string? ManifestChecksum { get; set; }

    /// <summary>
    /// Username of the administrator who performed the operation.
    /// </summary>
    public string PerformedBy { get; set; } = string.Empty;

    /// <summary>
    /// User ID of the administrator (if available).
    /// </summary>
    public Guid? PerformedByUserId { get; set; }

    /// <summary>
    /// IP address from which the operation was performed.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User-Agent header for browser/device identification.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Timestamp when the operation occurred (UTC).
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
