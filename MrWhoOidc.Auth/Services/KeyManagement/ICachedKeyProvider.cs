using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.Auth.Services.KeyManagement;

/// <summary>
/// Provides cached access to signing keys to avoid database roundtrips and expensive crypto object instantiation.
/// </summary>
public interface ICachedKeyProvider
{
    /// <summary>
    /// Gets the current active signing key.
    /// </summary>
    Task<SecurityKey> GetActiveSigningKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all public keys for JWKS.
    /// </summary>
    Task<IReadOnlyCollection<JsonWebKey>> GetPublicJwksAsync(CancellationToken ct = default);

    /// <summary>
    /// Invalidates the cache, forcing a reload on next access.
    /// </summary>
    void InvalidateCache();
}
