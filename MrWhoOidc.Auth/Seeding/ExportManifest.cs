using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Root container for exported OIDC configuration.
/// Wraps a SeedManifest with metadata for traceability and validation.
/// </summary>
public sealed record ExportManifest
{
    /// <summary>
    /// JSON Schema URL for validation.
    /// </summary>
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://mrwhooidc.io/schemas/export/v1";

    /// <summary>
    /// Export manifest format version.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Type of export: "tenant", "realm", "client", or "provider".
    /// </summary>
    [JsonPropertyName("exportType")]
    public string ExportType { get; init; } = "tenant";

    /// <summary>
    /// Export mode: "obfuscated" or "full".
    /// </summary>
    [JsonPropertyName("exportMode")]
    public string ExportMode { get; init; } = "obfuscated";

    /// <summary>
    /// Metadata about the export operation.
    /// </summary>
    [JsonPropertyName("metadata")]
    public ExportMetadata Metadata { get; init; } = new();

    /// <summary>
    /// The actual configuration data (SeedManifest structure).
    /// </summary>
    [JsonPropertyName("data")]
    public SeedManifest Data { get; init; } = new();

    /// <summary>
    /// Marker value used for obfuscated secrets.
    /// </summary>
    public const string ObfuscatedMarker = "***OBFUSCATED***";

    /// <summary>
    /// Checks if a value is an obfuscated placeholder.
    /// </summary>
    public static bool IsObfuscated(string? value) => value == ObfuscatedMarker;

    /// <summary>
    /// Returns the obfuscated marker if the value is not empty, otherwise null.
    /// </summary>
    public static string? ObfuscateSecret(string? value) =>
        string.IsNullOrEmpty(value) ? null : ObfuscatedMarker;
}
