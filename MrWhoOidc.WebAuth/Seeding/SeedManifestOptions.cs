namespace MrWhoOidc.WebAuth.Seeding;

public sealed class SeedManifestOptions
{
    /// <summary>
    /// Enables applying a seed manifest in dev/test environments.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// If set, loads the manifest from this file path.
    /// Suitable for Docker volume mounts.
    /// </summary>
    public string? ManifestPath { get; set; }

    /// <summary>
    /// If set, loads the manifest directly from this JSON string.
    /// </summary>
    public string? ManifestJson { get; set; }

    /// <summary>
    /// If set, loads the manifest from a Base64-encoded JSON string.
    /// Useful when providing seed data via .env files.
    /// </summary>
    public string? ManifestBase64 { get; set; }

    /// <summary>
    /// When true, updates existing records when properties are supplied in the manifest.
    /// When false, the applier prefers "create if missing" and only backfills empty fields.
    /// </summary>
    public bool AllowUpdates { get; set; } = false;

    /// <summary>
    /// When true, overwrites existing client secrets when clientSecret is supplied.
    /// Default is false to avoid unexpectedly changing secrets.
    /// </summary>
    public bool OverwriteClientSecrets { get; set; } = false;
}
