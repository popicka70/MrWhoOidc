using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for authenticating users against global UserAccount credentials.
/// Implements lockout logic with configurable threshold and duration.
/// </summary>
internal sealed class GlobalAuthenticationService(
    IUserAccountService userAccountService,
    IPasswordHasher passwordHasher,
    OidcMetrics metrics,
    ILogger<GlobalAuthenticationService> logger) : IGlobalAuthenticationService
{
    /// <summary>
    /// Maximum failed login attempts before lockout.
    /// </summary>
    private const int MaxFailedAttempts = 5;

    /// <summary>
    /// Lockout duration after exceeding max failed attempts.
    /// </summary>
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public async Task<GlobalAuthenticationResult> AuthenticateAsync(
        string usernameOrEmail,
        string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogDebug("Authentication failed: empty credentials");
            metrics.GlobalAuthFailure("empty_credentials");
            return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.InvalidPassword);
        }

        // Find account by username or email
        var account = await userAccountService.FindByUsernameOrEmailAsync(usernameOrEmail, ct).ConfigureAwait(false);
        if (account is null)
        {
            logger.LogDebug("Authentication failed: user not found for identifier {IdentifierHash}",
                HashForLog(usernameOrEmail));
            metrics.GlobalAuthFailure("user_not_found");
            return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.UserNotFound);
        }

        // Check if account is locked out
        if (await IsLockedOutAsync(account.Id, ct).ConfigureAwait(false))
        {
            logger.LogWarning("Authentication failed: account {AccountId} is locked out until {LockedUntil}",
                account.Id, account.LockedOutUntil);
            metrics.GlobalAuthFailure("account_locked");
            metrics.GlobalAccountLockout();
            return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.AccountLocked, account.LockedOutUntil);
        }

        // Verify password
        var isValid = passwordHasher.Verify(password, account.PasswordHash);
        if (!isValid)
        {
            logger.LogDebug("Authentication failed: invalid password for account {AccountId}", account.Id);
            await RecordFailedAttemptAsync(account.Id, ct).ConfigureAwait(false);
            metrics.GlobalAuthFailure("invalid_password");
            return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.InvalidPassword);
        }

        // Get active tenant memberships
        var memberships = await userAccountService.GetActiveMembershipsAsync(account.Id, ct).ConfigureAwait(false);
        if (memberships.Count == 0)
        {
            logger.LogWarning("Authentication failed: account {AccountId} has no active tenant memberships", account.Id);
            metrics.GlobalAuthFailure("no_active_memberships");
            return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.NoActiveMemberships);
        }

        // Clear failed attempts on successful login
        await ClearFailedAttemptsAsync(account.Id, ct).ConfigureAwait(false);

        // Check if MFA is required
        if (account.TotpEnabled && !string.IsNullOrEmpty(account.TotpSecret))
        {
            logger.LogDebug("Authentication requires MFA for account {AccountId}", account.Id);
            metrics.GlobalAuthFailure("mfa_required");
            return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.MfaRequired);
        }

        logger.LogInformation("Authentication succeeded for account {AccountId} with {MembershipCount} active memberships",
            account.Id, memberships.Count);
        metrics.GlobalAuthSuccess();

        return GlobalAuthenticationResult.Success(account, memberships);
    }

    public async Task RecordFailedAttemptAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await userAccountService.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            logger.LogWarning("Cannot record failed attempt: account {AccountId} not found", accountId);
            return;
        }

        var failedAttempts = account.FailedLoginAttempts + 1;
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? lockedOutUntil = null;

        if (failedAttempts >= MaxFailedAttempts)
        {
            lockedOutUntil = now.Add(LockoutDuration);
            logger.LogWarning("Account {AccountId} locked out until {LockedUntil} after {FailedAttempts} failed attempts",
                accountId, lockedOutUntil, failedAttempts);
            metrics.GlobalAccountLockout();
        }
        else
        {
            logger.LogDebug("Account {AccountId} has {FailedAttempts}/{MaxAttempts} failed attempts",
                accountId, failedAttempts, MaxFailedAttempts);
        }

        await userAccountService.UpdateLockoutAsync(accountId, failedAttempts, now, lockedOutUntil, ct).ConfigureAwait(false);
    }

    public async Task ClearFailedAttemptsAsync(Guid accountId, CancellationToken ct = default)
    {
        await userAccountService.UpdateLockoutAsync(accountId, 0, null, null, ct).ConfigureAwait(false);
        logger.LogDebug("Cleared failed login attempts for account {AccountId}", accountId);
    }

    public async Task<bool> IsLockedOutAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await userAccountService.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return false;
        }

        return account.LockedOutUntil.HasValue && account.LockedOutUntil.Value > DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Hashes an identifier for safe logging (PII protection).
    /// </summary>
    private static string HashForLog(string value)
    {
        if (string.IsNullOrEmpty(value)) return "[empty]";
        // Simple hash for logging - not cryptographically secure, just for log correlation
        var hash = value.GetHashCode(StringComparison.OrdinalIgnoreCase);
        return $"[hash:{hash:X8}]";
    }
}
