namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for authenticating users against global UserAccount credentials.
/// This service operates independently of tenant context.
/// </summary>
public interface IGlobalAuthenticationService
{
    /// <summary>
    /// Authenticates a user by username/email and password.
    /// </summary>
    /// <param name="usernameOrEmail">Username or email to authenticate</param>
    /// <param name="password">Plain-text password</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Authentication result with account details or failure reason</returns>
    Task<GlobalAuthenticationResult> AuthenticateAsync(
        string usernameOrEmail,
        string password,
        CancellationToken ct = default);

    /// <summary>
    /// Records a failed login attempt for lockout tracking.
    /// </summary>
    /// <param name="accountId">UserAccount ID</param>
    /// <param name="ct">Cancellation token</param>
    Task RecordFailedAttemptAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Clears failed attempt counter after successful login.
    /// </summary>
    /// <param name="accountId">UserAccount ID</param>
    /// <param name="ct">Cancellation token</param>
    Task ClearFailedAttemptsAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Checks if an account is currently locked out.
    /// </summary>
    /// <param name="accountId">UserAccount ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if locked out, false otherwise</returns>
    Task<bool> IsLockedOutAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Finds a UserAccount by email address.
    /// </summary>
    /// <param name="email">Email address to search for</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The UserAccount if found, null otherwise</returns>
    Task<Persistence.UserAccount?> FindAccountByEmailAsync(string email, CancellationToken ct = default);
}
