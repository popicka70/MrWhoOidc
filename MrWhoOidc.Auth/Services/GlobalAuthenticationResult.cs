using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Result of a global authentication attempt against UserAccount credentials.
/// </summary>
public sealed record GlobalAuthenticationResult
{
    /// <summary>
    /// Whether authentication succeeded.
    /// </summary>
    public bool Succeeded { get; init; }

    /// <summary>
    /// The authenticated UserAccount. Failed password/user lookups leave this null;
    /// MFA-required results include it because the password step already succeeded.
    /// </summary>
    public UserAccount? Account { get; init; }

    /// <summary>
    /// Available tenant memberships for the authenticated user.
    /// </summary>
    public IReadOnlyList<UserTenantMembership> Memberships { get; init; }
        = Array.Empty<UserTenantMembership>();

    /// <summary>
    /// Failure reason if authentication failed.
    /// </summary>
    public AuthenticationFailureReason? FailureReason { get; init; }

    /// <summary>
    /// When the account is locked until (only set when FailureReason is AccountLocked).
    /// </summary>
    public DateTimeOffset? LockedUntil { get; init; }

    /// <summary>
    /// Creates a successful authentication result.
    /// </summary>
    public static GlobalAuthenticationResult Success(
        UserAccount account,
        IReadOnlyList<UserTenantMembership> memberships)
        => new()
        {
            Succeeded = true,
            Account = account,
            Memberships = memberships
        };

    /// <summary>
    /// Creates an MFA-required authentication result after successful password verification.
    /// </summary>
    public static GlobalAuthenticationResult MfaRequired(
        UserAccount account,
        IReadOnlyList<UserTenantMembership> memberships)
        => new()
        {
            Succeeded = false,
            Account = account,
            Memberships = memberships,
            FailureReason = AuthenticationFailureReason.MfaRequired
        };

    /// <summary>
    /// Creates a failed authentication result.
    /// </summary>
    public static GlobalAuthenticationResult Failure(AuthenticationFailureReason reason, DateTimeOffset? lockedUntil = null)
        => new()
        {
            Succeeded = false,
            FailureReason = reason,
            LockedUntil = lockedUntil
        };
}

/// <summary>
/// Reasons for authentication failure.
/// </summary>
public enum AuthenticationFailureReason
{
    /// <summary>User not found by username or email.</summary>
    UserNotFound,

    /// <summary>Password did not match.</summary>
    InvalidPassword,

    /// <summary>Account is locked due to too many failed attempts.</summary>
    AccountLocked,

    /// <summary>Account has no active tenant memberships.</summary>
    NoActiveMemberships,

    /// <summary>MFA is required but not completed.</summary>
    MfaRequired
}
