using System.Security.Cryptography;

namespace LicensingService.Core.Crypto;

/// <summary>
/// Service for managing signing keys.
/// </summary>
public interface ISigningKeyService
{
    /// <summary>
    /// Gets the current active signing key for creating new licenses.
    /// </summary>
    Task<(ECDsa Key, string Kid)> GetActiveSigningKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all public keys for verification (JWKS endpoint).
    /// </summary>
    Task<IReadOnlyList<(ECDsa Key, string Kid, string Algorithm)>> GetPublicKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotates the signing key - creates new active key, marks old as rotated.
    /// </summary>
    Task<string> RotateKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes or loads the signing key from configuration/storage.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
