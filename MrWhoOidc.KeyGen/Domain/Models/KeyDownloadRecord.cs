namespace MrWhoOidc.KeyGen.Domain.Models;

/// <summary>
/// Tracks when keys were downloaded for audit and compliance purposes.
/// </summary>
public class KeyDownloadRecord
{
    /// <summary>
    /// Unique identifier for the download record (UUIDv7).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Reference to the key pair that was downloaded.
    /// </summary>
    public Guid KeyPairMetadataId { get; set; }

    /// <summary>
    /// Type of download (PrivateKey, PublicKey).
    /// </summary>
    public required string DownloadType { get; set; }

    /// <summary>
    /// When the download occurred.
    /// </summary>
    public DateTimeOffset DownloadedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// User/identity who downloaded the key (if auth is implemented).
    /// </summary>
    public string? DownloadedBy { get; set; }

    /// <summary>
    /// IP address of the requester (for audit).
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent string (for audit).
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Navigation property to the key pair metadata.
    /// </summary>
    public KeyPairMetadata? KeyPairMetadata { get; set; }
}
