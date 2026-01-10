using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IPlatformInitialAccessTokenService
{
    /// <summary>
    /// Returns active (not revoked) initial access tokens.
    /// Tokens are returned without any plaintext value (only metadata + hash).
    /// </summary>
    Task<IReadOnlyList<PlatformInitialAccessToken>> GetActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a new initial access token and returns the plaintext token once.
    /// The hash is stored.
    /// </summary>
    Task<(PlatformInitialAccessToken Entity, string PlaintextToken)> CreateAsync(string? description, string? createdBy, CancellationToken ct = default);

    /// <summary>
    /// Revokes an existing token.
    /// </summary>
    Task<bool> RevokeAsync(Guid id, string? revokedBy, CancellationToken ct = default);

    /// <summary>
    /// Validates a submitted plaintext token against active tokens.
    /// </summary>
    Task<bool> ValidateAsync(string plaintextToken, CancellationToken ct = default);
}
