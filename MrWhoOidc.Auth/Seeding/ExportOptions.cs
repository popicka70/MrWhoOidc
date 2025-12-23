namespace MrWhoOidc.Auth.Seeding;

/// <summary>
/// Options for configuring how configuration exports are generated.
/// </summary>
public sealed record ExportOptions
{
    /// <summary>
    /// Specifies how secrets should be handled in the export.
    /// Default is Obfuscated (secrets replaced with placeholder).
    /// </summary>
    public ExportMode Mode { get; init; } = ExportMode.Obfuscated;

    /// <summary>
    /// Whether to include metadata (timestamp, exporter, source system) in the export.
    /// Default is true.
    /// </summary>
    public bool IncludeMetadata { get; init; } = true;

    /// <summary>
    /// Whether to generate and include a SHA-256 checksum of the data section.
    /// Default is true.
    /// </summary>
    public bool IncludeChecksum { get; init; } = true;

    /// <summary>
    /// Whether to format JSON output with indentation for readability.
    /// Default is true.
    /// </summary>
    public bool PrettyPrint { get; init; } = true;

    /// <summary>
    /// Optional username of the admin performing the export.
    /// Used for metadata and audit logging.
    /// </summary>
    public string? ExportedBy { get; init; }

    /// <summary>
    /// Optional identifier for the source system performing the export.
    /// Used for metadata (e.g., hostname or instance name).
    /// </summary>
    public string? SourceSystem { get; init; }

    /// <summary>
    /// Default export options with obfuscated secrets and metadata included.
    /// </summary>
    public static ExportOptions Default => new();

    /// <summary>
    /// Export options for full export with hashed secrets included.
    /// </summary>
    public static ExportOptions Full => new() { Mode = ExportMode.Full };
}
