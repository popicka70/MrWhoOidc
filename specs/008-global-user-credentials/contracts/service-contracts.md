# Service Contracts: Global User Credentials

**Feature**: 008-global-user-credentials  
**Date**: 2025-12-05

## Overview

This feature introduces internal service contracts (C# interfaces) rather than HTTP API contracts, as the global authentication service is consumed internally by login handlers.

## IGlobalAuthenticationService

**Location**: `MrWhoOidc.Auth/Services/GlobalAuthenticationService.cs`

**Purpose**: Authenticate users against global `UserAccount` credentials, independent of tenant context.

```csharp
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
}
```

---

## GlobalAuthenticationResult

**Location**: `MrWhoOidc.Auth/Services/GlobalAuthenticationResult.cs`

```csharp
namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Result of a global authentication attempt.
/// </summary>
public sealed record GlobalAuthenticationResult
{
    /// <summary>
    /// Whether authentication succeeded.
    /// </summary>
    public bool Succeeded { get; init; }

    /// <summary>
    /// The authenticated UserAccount (null if failed).
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
    /// Creates a failed authentication result.
    /// </summary>
    public static GlobalAuthenticationResult Failure(AuthenticationFailureReason reason) 
        => new() 
        { 
            Succeeded = false, 
            FailureReason = reason 
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
```

---

## IUserAccountService (Extended)

**Location**: `MrWhoOidc.Auth/Services/UserAccountService.cs`

**Extensions to existing interface**:

```csharp
public interface IUserAccountService
{
    // Existing methods...
    Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<UserAccount> CreateAsync(UserAccount account, CancellationToken ct = default);

    // New methods for global credentials...
    
    /// <summary>
    /// Finds a UserAccount by normalized email.
    /// </summary>
    Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Finds a UserAccount by username or email.
    /// </summary>
    Task<UserAccount?> FindByUsernameOrEmailAsync(
        string usernameOrEmail, 
        CancellationToken ct = default);

    /// <summary>
    /// Updates the password for a UserAccount.
    /// </summary>
    Task UpdatePasswordAsync(
        Guid accountId, 
        string newPasswordHash, 
        string? salt, 
        string algorithm, 
        CancellationToken ct = default);

    /// <summary>
    /// Gets all active tenant memberships for an account.
    /// </summary>
    Task<IReadOnlyList<UserTenantMembership>> GetActiveMembershipsAsync(
        Guid accountId, 
        CancellationToken ct = default);

    /// <summary>
    /// Updates lockout fields for an account.
    /// </summary>
    Task UpdateLockoutAsync(
        Guid accountId, 
        int failedAttempts, 
        DateTimeOffset? lockedOutUntil, 
        CancellationToken ct = default);
}
```

---

## Password Policy Contract

**Location**: `MrWhoOidc.Auth/Services/IGlobalPasswordPolicyService.cs`

```csharp
namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for validating passwords against the global password policy.
/// Unlike tenant-specific policies, this applies platform-wide.
/// </summary>
public interface IGlobalPasswordPolicyService
{
    /// <summary>
    /// Validates a password against the global policy.
    /// </summary>
    /// <param name="password">Password to validate</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result</returns>
    Task<PasswordValidationResult> ValidateAsync(
        string password, 
        CancellationToken ct = default);
}
```

---

## Integration Points

### Login Handler Integration

```csharp
// In Login.cshtml.cs OnPostAsync()

// OLD (per-tenant):
var user = await users.FindByUsernameAsync(Username);
if (user is null || !await users.VerifyPasswordAsync(user, Password))
    return InvalidCredentials();

// NEW (global):
var result = await globalAuth.AuthenticateAsync(Username, Password);
if (!result.Succeeded)
    return HandleFailure(result.FailureReason);

// Resolve tenant membership based on context
var membership = ResolveMembership(result.Memberships, tenantContext);
return await CompleteSignInAsync(result.Account, membership);
```

### Password Change Integration

```csharp
// In Profile/ChangePassword.cshtml.cs

// Hash new password
var hash = hasher.Hash(NewPassword);

// Update global account
await accountService.UpdatePasswordAsync(
    currentUser.AccountId, 
    hash.Hash, 
    hash.Salt, 
    hash.Algorithm);

// Dual-write to legacy User (during transition)
if (featureOptions.DualWriteEnabled)
{
    await userService.UpdatePasswordHashAsync(
        currentUser.LegacyUserId, 
        hash.Hash);
}
```

---

## Error Responses

| Failure Reason | User Message | Log Level |
|----------------|--------------|-----------|
| `UserNotFound` | "Invalid username or password" | Warning |
| `InvalidPassword` | "Invalid username or password" | Warning |
| `AccountLocked` | "Account locked. Try again in X minutes." | Warning |
| `NoActiveMemberships` | "No active access. Contact administrator." | Warning |
| `MfaRequired` | Redirect to MFA page | Info |

**Security Note**: `UserNotFound` and `InvalidPassword` return the same message to prevent user enumeration.
