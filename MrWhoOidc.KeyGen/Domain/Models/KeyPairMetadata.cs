namespace MrWhoOidc.KeyGen.Domain.Models;

/// <summary>
/// Represents a generated cryptographic key pair with metadata for tracking and audit purposes.
/// </summary>
public class KeyPairMetadata
{
    /// <summary>
    /// Unique identifier for the key pair metadata record (UUIDv7).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Key ID used in JWK/JWKS (e.g., "7f9c8b3a-5d2e-4f1b-9c8a-3e5d7f9c8b3a").
    /// </summary>
    public required string Kid { get; set; }

    /// <summary>
    /// Signing algorithm (RS256, RS384, RS512, ES256, ES384, ES512, PS256).
    /// </summary>
    public required string Algorithm { get; set; }

    /// <summary>
    /// Key type (RSA, EC).
    /// </summary>
    public required string KeyType { get; set; }

    /// <summary>
    /// Key size in bits for RSA (2048, 3072, 4096); null for EC.
    /// </summary>
    public int? KeySize { get; set; }

    /// <summary>
    /// Elliptic curve name for EC keys (P-256, P-384, P-521); null for RSA.
    /// </summary>
    public string? Curve { get; set; }

    /// <summary>
    /// Public key in JWKS format (JSON string).
    /// </summary>
    public required string PublicKeyJwks { get; set; }

    /// <summary>
    /// When the key pair was generated.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Key status (Active, Revoked).
    /// </summary>
    public string Status { get; set; } = "Active";

    /// <summary>
    /// When the key was revoked (null if active).
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// User/identity who generated the key (if auth is implemented).
    /// </summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Number of times the private key was downloaded.
    /// </summary>
    public int DownloadCount { get; set; } = 0;

    /// <summary>
    /// Navigation property for download records.
    /// </summary>
    public ICollection<KeyDownloadRecord> DownloadRecords { get; set; } = new List<KeyDownloadRecord>();
}
