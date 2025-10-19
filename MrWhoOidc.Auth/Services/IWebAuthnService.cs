using Fido2NetLib;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for handling WebAuthn/FIDO2 authentication operations.
/// </summary>
public interface IWebAuthnService
{
    /// <summary>
    /// Creates a registration challenge for a user to register a new WebAuthn credential.
    /// </summary>
    /// <param name="user">The user registering a credential</param>
    /// <param name="excludeCredentials">Whether to exclude existing credentials</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Credential creation options and challenge session</returns>
    Task<(CredentialCreateOptions options, string sessionId)> CreateRegistrationChallengeAsync(
        User user, 
        bool excludeCredentials = true, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the registration of a WebAuthn credential.
    /// </summary>
    /// <param name="user">The user registering the credential</param>
    /// <param name="attestationResponse">The attestation response from the client</param>
    /// <param name="sessionId">The challenge session ID</param>
    /// <param name="friendlyName">User-friendly name for the credential</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Registration result and credential ID</returns>
    Task<(bool success, string? credentialId, string? errorMessage)> CompleteRegistrationAsync(
        User user,
        AuthenticatorAttestationRawResponse attestationResponse,
        string sessionId,
        string? friendlyName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an authentication challenge for WebAuthn login.
    /// </summary>
    /// <param name="username">Username for authentication (optional for usernameless flow)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Assertion options and challenge session</returns>
    Task<(AssertionOptions options, string sessionId)> CreateAuthenticationChallengeAsync(
        string? username = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes WebAuthn authentication.
    /// </summary>
    /// <param name="assertionResponse">The assertion response from the client</param>
    /// <param name="sessionId">The challenge session ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Authentication result and user information</returns>
    Task<(bool success, User? user, string? errorMessage)> CompleteAuthenticationAsync(
        AuthenticatorAssertionRawResponse assertionResponse,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all WebAuthn credentials for a user.
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of user's WebAuthn credentials</returns>
    Task<IReadOnlyList<WebAuthnCredential>> GetUserCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a WebAuthn credential.
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="credentialId">Credential ID to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if credential was removed</returns>
    Task<bool> RemoveCredentialAsync(
        Guid userId,
        Guid credentialId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a credential's friendly name.
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="credentialId">Credential ID</param>
    /// <param name="friendlyName">New friendly name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if updated successfully</returns>
    Task<bool> UpdateCredentialNameAsync(
        Guid userId,
        Guid credentialId,
        string friendlyName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user has any active WebAuthn credentials.
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if user has WebAuthn credentials</returns>
    Task<bool> HasWebAuthnCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}