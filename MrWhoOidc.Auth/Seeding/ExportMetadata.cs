using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Metadata about an export operation for traceability and validation.
/// </summary>
public sealed record ExportMetadata
{
    /// <summary>
    /// UTC timestamp when the export was created.
    /// </summary>
    [JsonPropertyName("exportedAt")]
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Username of the administrator who performed the export.
    /// </summary>
    [JsonPropertyName("exportedBy")]
    public string? ExportedBy { get; init; }

    /// <summary>
    /// Identifier of the source system (hostname or instance name).
    /// </summary>
    [JsonPropertyName("sourceSystem")]
    public string? SourceSystem { get; init; }

    /// <summary>
    /// Version of MrWhoOidc that created the export.
    /// </summary>
    [JsonPropertyName("sourceVersion")]
    public string? SourceVersion { get; init; }

    /// <summary>
    /// Tenant slug for context when exporting realm/client/provider.
    /// </summary>
    [JsonPropertyName("sourceTenant")]
    public string? SourceTenant { get; init; }

    /// <summary>
    /// SHA-256 hash of the data section for integrity verification.
    /// Format: "sha256:{hash}"
    /// </summary>
    [JsonPropertyName("checksum")]
    public string? Checksum { get; init; }
}
