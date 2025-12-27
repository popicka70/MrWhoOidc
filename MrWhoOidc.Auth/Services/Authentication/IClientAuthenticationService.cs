namespace MrWhoOidc.Auth.Services.Authentication;

/// <summary>
/// Domain service for authenticating clients using various methods (secret, JWT, mTLS).
/// Decoupled from HTTP context.
/// </summary>
public interface IClientAuthenticationService
{
    /// <summary>
    /// Authenticates a client based on the provided credentials.
    /// </summary>
    Task<ClientAuthResult> AuthenticateAsync(ClientCredentialInput input, CancellationToken ct = default);
}
