namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Domain service for creating RFC 8225 compliant logout tokens for back-channel logout.
/// </summary>
public interface ILogoutTokenService
{
    /// <summary>
    /// Creates a logout_token JWT with the required claims per OIDC Back-Channel Logout spec.
    /// Returns null if neither sub nor sid can be included (spec requires at least one).
    /// </summary>
    Task<string?> CreateLogoutTokenAsync(string issuer, string audienceClientId, string? sub, string? sid, CancellationToken ct = default);
}
