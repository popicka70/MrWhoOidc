namespace LicensingService.Core.Entities;

/// <summary>
/// Signing key for license tokens.
/// Only public keys are stored in the database for JWKS endpoint.
/// Private keys are stored externally (file or secret manager).
/// </summary>
public class SigningKey
{
    /// <summary>Unique identifier (UUIDv7).</summary>
    public Guid Id { get; set; }

    /// <summary>Key identifier used in JWT header (kid claim).</summary>
    public string Kid { get; set; } = string.Empty;

    /// <summary>Signing algorithm (e.g., ES256).</summary>
    public string Algorithm { get; set; } = "ES256";

    /// <summary>Public key in JWK format (JSON).</summary>
    public string PublicKeyJwks { get; set; } = string.Empty;

    /// <summary>Current status of the key.</summary>
    public SigningKeyStatus Status { get; set; } = SigningKeyStatus.Active;

    /// <summary>Key creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the key was rotated out.</summary>
    public DateTimeOffset? RotatedAt { get; set; }
}
