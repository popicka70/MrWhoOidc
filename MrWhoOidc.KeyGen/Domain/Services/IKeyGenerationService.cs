namespace MrWhoOidc.KeyGen.Domain.Services;

/// <summary>
/// Service for generating cryptographic key pairs.
/// </summary>
public interface IKeyGenerationService
{
    /// <summary>
    /// Generates a cryptographic key pair and stores metadata.
    /// </summary>
    /// <param name="algorithm">Signing algorithm (RS256, RS384, RS512, ES256, ES384, ES512, PS256).</param>
    /// <param name="keyType">Key type (RSA, EC).</param>
    /// <param name="keySize">Key size in bits for RSA (2048, 3072, 4096); null for EC.</param>
    /// <param name="curve">Curve name for EC keys (P-256, P-384, P-521); null for RSA.</param>
    /// <param name="createdBy">Optional user/identity who generated the key.</param>
    /// <returns>Tuple containing kid, private key JWK, and public key JWKS.</returns>
    Task<(string Kid, string PrivateKeyJwk, string PublicKeyJwks)> GenerateKeyPairAsync(
        string algorithm,
        string keyType,
        int? keySize,
        string? curve,
        string? createdBy = null);

    /// <summary>
    /// Revokes a key pair, preventing further downloads of private keys.
    /// </summary>
    /// <param name="kid">The key identifier to revoke.</param>
    /// <param name="revokedBy">Optional user/identity who revoked the key.</param>
    /// <returns>True if the key was revoked successfully; false if not found.</returns>
    Task<bool> RevokeKeyAsync(string kid, string? revokedBy = null);
}
