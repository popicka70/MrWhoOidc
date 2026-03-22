using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.Webauthn;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for handling WebAuthn/FIDO2 authentication operations.
/// </summary>
public interface IWebAuthnService
{
    /// <summary>
    /// Creates a registration challenge for a user to register a new WebAuthn credential.
    /// </summary>
    Task<(WebAuthnRegistrationOptions options, string sessionId)> CreateRegistrationChallengeAsync(
        User user,
        bool excludeCredentials = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the registration of a WebAuthn credential.
    /// </summary>
    Task<(bool success, string? credentialId, string? errorMessage)> CompleteRegistrationAsync(
        User user,
        WebAuthnAttestationResponse attestationResponse,
        string sessionId,
        string? friendlyName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an authentication challenge for WebAuthn login.
    /// </summary>
    Task<(WebAuthnAssertionOptions options, string sessionId)> CreateAuthenticationChallengeAsync(
        string? username = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes WebAuthn authentication.
    /// </summary>
    Task<(bool success, User? user, string? errorMessage)> CompleteAuthenticationAsync(
        WebAuthnAssertionResponse assertionResponse,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets all WebAuthn credentials for a user.</summary>
    Task<IReadOnlyList<WebAuthnCredential>> GetUserCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a WebAuthn credential.</summary>
    Task<bool> RemoveCredentialAsync(
        Guid userId,
        Guid credentialId,
        CancellationToken cancellationToken = default);

    /// <summary>Updates a credential's friendly name.</summary>
    Task<bool> UpdateCredentialNameAsync(
        Guid userId,
        Guid credentialId,
        string friendlyName,
        CancellationToken cancellationToken = default);

    /// <summary>Returns true if the user has at least one active WebAuthn credential.</summary>
    Task<bool> HasWebAuthnCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
