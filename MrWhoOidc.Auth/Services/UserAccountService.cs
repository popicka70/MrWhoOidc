using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IUserAccountService
{
    Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<UserAccount> CreateAsync(UserAccount account, CancellationToken ct = default);

    // New methods for global credentials

    /// <summary>
    /// Finds a UserAccount by normalized email.
    /// </summary>
    Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Finds a UserAccount by username or email.
    /// </summary>
    Task<UserAccount?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default);

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
        DateTimeOffset? lastFailedAt,
        DateTimeOffset? lockedOutUntil,
        CancellationToken ct = default);

    /// <summary>
    /// Enables TOTP MFA for an account by setting the secret.
    /// </summary>
    Task EnableMfaAsync(Guid accountId, string totpSecret, CancellationToken ct = default);

    /// <summary>
    /// Confirms TOTP MFA enrollment after successful code verification.
    /// </summary>
    Task ConfirmMfaAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Disables TOTP MFA for an account.
    /// </summary>
    Task DisableMfaAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Gets the MFA status and secret for an account.
    /// </summary>
    Task<(bool Enabled, string? Secret)> GetMfaStatusAsync(Guid accountId, CancellationToken ct = default);
}

internal sealed class UserAccountService(AuthDbContext dbContext, ILogger<UserAccountService> logger) : IUserAccountService
{
    public async Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await dbContext.UserAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);

    public async Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        return await dbContext.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Username == username, ct)
            .ConfigureAwait(false);
    }

    public async Task<UserAccount> CreateAsync(UserAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        dbContext.UserAccounts.Add(account);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return account;
    }

    public async Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var normalizedEmail = email.Trim().ToLowerInvariant();
        return await dbContext.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, ct)
            .ConfigureAwait(false);
    }

    public async Task<UserAccount?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail)) return null;

        var trimmed = usernameOrEmail.Trim();
        var normalized = trimmed.ToLowerInvariant();

        // Try by username first (exact match), then by normalized email
        return await dbContext.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Username == trimmed || x.NormalizedEmail == normalized, ct)
            .ConfigureAwait(false);
    }

    public async Task UpdatePasswordAsync(
        Guid accountId,
        string newPasswordHash,
        string? salt,
        string algorithm,
        CancellationToken ct = default)
    {
        logger.LogInformation("🔄 [UpdatePasswordAsync] Starting for account {AccountId}", accountId);
        
        var account = await dbContext.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            logger.LogError("❌ [UpdatePasswordAsync] Account {AccountId} not found!", accountId);
            throw new InvalidOperationException($"UserAccount {accountId} not found.");
        }

        var oldHashPrefix = account.PasswordHash?.Length > 20 ? account.PasswordHash[..20] : account.PasswordHash ?? "(none)";
        logger.LogInformation("🔄 [UpdatePasswordAsync] Found account {AccountId}, old hash prefix: '{OldHash}...'", accountId, oldHashPrefix);

        account.PasswordHash = newPasswordHash;
        account.PasswordSalt = salt;
        account.HashAlgorithm = algorithm;
        account.PasswordUpdatedAt = DateTimeOffset.UtcNow;
        // Clear lockout state on password change
        account.FailedLoginAttempts = 0;
        account.LastFailedLoginAt = null;
        account.LockedOutUntil = null;

        var newHashPrefix = newPasswordHash.Length > 20 ? newPasswordHash[..20] : newPasswordHash;
        logger.LogInformation("🔄 [UpdatePasswordAsync] About to SaveChanges - new hash prefix: '{NewHash}...'", newHashPrefix);
        
        var changes = await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("✅ [UpdatePasswordAsync] SaveChangesAsync returned {Changes} changes", changes);
    }

    public async Task<IReadOnlyList<UserTenantMembership>> GetActiveMembershipsAsync(
        Guid accountId,
        CancellationToken ct = default)
    {
        return await dbContext.UserTenantMemberships
            .AsNoTracking()
            .Include(x => x.Tenant)
            .Where(x => x.UserAccountId == accountId
                && x.Status == TenantMembershipStatus.Active
                && (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task UpdateLockoutAsync(
        Guid accountId,
        int failedAttempts,
        DateTimeOffset? lastFailedAt,
        DateTimeOffset? lockedOutUntil,
        CancellationToken ct = default)
    {
        var account = await dbContext.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            throw new InvalidOperationException($"UserAccount {accountId} not found.");
        }

        account.FailedLoginAttempts = failedAttempts;
        account.LastFailedLoginAt = lastFailedAt;
        account.LockedOutUntil = lockedOutUntil;

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task EnableMfaAsync(Guid accountId, string totpSecret, CancellationToken ct = default)
    {
        var account = await dbContext.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            throw new InvalidOperationException($"UserAccount {accountId} not found.");
        }

        account.TotpSecret = totpSecret;
        // Don't enable yet - wait for confirmation
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ConfirmMfaAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await dbContext.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            throw new InvalidOperationException($"UserAccount {accountId} not found.");
        }

        if (string.IsNullOrWhiteSpace(account.TotpSecret))
        {
            throw new InvalidOperationException($"UserAccount {accountId} does not have a TOTP secret set.");
        }

        account.TotpEnabled = true;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DisableMfaAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await dbContext.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            throw new InvalidOperationException($"UserAccount {accountId} not found.");
        }

        account.TotpEnabled = false;
        account.TotpSecret = null;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(bool Enabled, string? Secret)> GetMfaStatusAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await dbContext.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == accountId, ct)
            .ConfigureAwait(false);
        
        if (account is null)
        {
            throw new InvalidOperationException($"UserAccount {accountId} not found.");
        }

        return (account.TotpEnabled, account.TotpSecret);
    }
}
