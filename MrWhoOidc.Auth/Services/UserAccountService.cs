using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IUserAccountService
{
    Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The global account a per-tenant user belongs to, via <see cref="User.UserAccountId"/>. Only legacy rows
    /// without the link fall back to the home-user id and then email. Use this instead of looking an account up
    /// by the user's email: a per-tenant email is not proof of owning the account that has the same address.
    /// </summary>
    Task<UserAccount?> FindForUserAsync(User user, CancellationToken ct = default);
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

internal sealed class UserAccountService(AuthDbContext dbContext, ILogger<UserAccountService>? logger = null, ISecretProtector? secretProtector = null) : IUserAccountService
{
    public async Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => UnprotectTotpSecret(await dbContext.UserAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false));

    public async Task<UserAccount?> FindForUserAsync(User user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.UserAccountId is { } linkedId)
        {
            return await GetByIdAsync(linkedId, ct).ConfigureAwait(false);
        }

        return await GetByIdAsync(user.Id, ct).ConfigureAwait(false)
               ?? (string.IsNullOrEmpty(user.Email) ? null : await FindByEmailAsync(user.Email, ct).ConfigureAwait(false));
    }

    public async Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var account = await dbContext.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Username == username, ct)
            .ConfigureAwait(false);
        return UnprotectTotpSecret(account);
    }

    public async Task<UserAccount> CreateAsync(UserAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ProtectTotpSecret(account);
        dbContext.UserAccounts.Add(account);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        UnprotectTotpSecret(account);
        return account;
    }

    public async Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalizedEmail = EmailNormalizer.NormalizeForLookup(email);
        if (normalizedEmail is null) return null;
        var account = await dbContext.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, ct)
            .ConfigureAwait(false);
        return UnprotectTotpSecret(account);
    }

    public async Task<UserAccount?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail)) return null;

        var trimmed = usernameOrEmail.Trim();
        var normalized = EmailNormalizer.NormalizeForLookup(trimmed);

        // Try by username first (exact match), then by normalized email
        var account = await dbContext.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Username == trimmed || (normalized != null && x.NormalizedEmail == normalized), ct)
            .ConfigureAwait(false);
        return UnprotectTotpSecret(account);
    }

    public async Task UpdatePasswordAsync(
        Guid accountId,
        string newPasswordHash,
        string? salt,
        string algorithm,
        CancellationToken ct = default)
    {
        logger?.LogInformation("[UpdatePasswordAsync] Starting for account {AccountId}", accountId);

        var account = await dbContext.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            logger?.LogError("[UpdatePasswordAsync] Account {AccountId} not found", accountId);
            throw new InvalidOperationException($"UserAccount {accountId} not found.");
        }

        account.PasswordHash = newPasswordHash;
        account.PasswordSalt = salt;
        account.HashAlgorithm = algorithm;
        account.PasswordUpdatedAt = DateTimeOffset.UtcNow;
        // Clear lockout state on password change
        account.FailedLoginAttempts = 0;
        account.LastFailedLoginAt = null;
        account.LockedOutUntil = null;

        // A credential change must end every existing session: whoever knew the old password (or holds a
        // stolen session) must not keep access. Rotating the stamp invalidates auth cookies on their next
        // validation; tokens issued to RPs are revoked across all of the account's tenants.
        account.SecurityStamp = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var revoked = await RevokeTokensForAccountAsync(account, ct).ConfigureAwait(false);
        logger?.LogInformation("[UpdatePasswordAsync] Revoked {Count} tokens for account {AccountId}", revoked, accountId);

        // Do not log any portion of the password hash, even a prefix.
        var changes = await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        logger?.LogInformation("[UpdatePasswordAsync] SaveChangesAsync returned {Changes} changes for account {AccountId}", changes, accountId);
    }

    /// <summary>
    /// Revokes all live tokens held by the account's per-tenant users: those linked by <c>User.UserAccountId</c>,
    /// plus legacy unlinked rows in the account's tenants that resolve to it by email (the same fallback as
    /// <see cref="FindForUserAsync"/>). Matching linked users by email/username missed a user whose tenant email
    /// had changed and revoked unrelated users sharing a username.
    /// </summary>
    private async Task<int> RevokeTokensForAccountAsync(UserAccount account, CancellationToken ct)
    {
        var tenantIds = await dbContext.UserTenantMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UserAccountId == account.Id)
            .Select(m => m.TenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var normalizedEmail = account.NormalizedEmail;
        var accountId = account.Id;
        var userIds = await dbContext.Users
            .IgnoreQueryFilters()
            .Where(u => u.UserAccountId == accountId ||
                        (u.UserAccountId == null &&
                         (u.Id == accountId ||
                          (tenantIds.Contains(u.TenantId) && normalizedEmail != null && u.NormalizedEmail == normalizedEmail))))
            .Select(u => u.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (userIds.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var live = dbContext.Tokens
            .IgnoreQueryFilters()
            .Where(t => userIds.Contains(t.UserId) && t.RevokedAt == null && t.ExpiresAt > now);

        if (dbContext.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var tokens = await live.ToListAsync(ct).ConfigureAwait(false);
            foreach (var token in tokens) token.RevokedAt = now;
            return tokens.Count;
        }

        return await live.ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, now), ct).ConfigureAwait(false);
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

        account.TotpSecret = secretProtector?.ProtectTotpSecret(totpSecret) ?? totpSecret;
        // New enrolments use the algorithm authenticator apps actually implement; a fresh secret starts with no used step.
        account.TotpAlgorithm = TotpAlgorithms.Default;
        account.TotpLastUsedStep = null;
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
        account.TotpAlgorithm = null;
        account.TotpLastUsedStep = null;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(bool Enabled, string? Secret)> GetMfaStatusAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await dbContext.UserAccounts
            .FirstOrDefaultAsync(x => x.Id == accountId, ct)
            .ConfigureAwait(false);

        if (account is null)
        {
            throw new InvalidOperationException($"UserAccount {accountId} not found.");
        }

        var secret = secretProtector?.UnprotectTotpSecret(account.TotpSecret) ?? account.TotpSecret;
        if (secretProtector is not null && !string.IsNullOrWhiteSpace(account.TotpSecret) && !secretProtector.IsProtected(account.TotpSecret))
        {
            account.TotpSecret = secretProtector.ProtectTotpSecret(secret!);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return (account.TotpEnabled, secret);
    }

    private UserAccount? UnprotectTotpSecret(UserAccount? account)
    {
        if (account is not null && !string.IsNullOrWhiteSpace(account.TotpSecret))
        {
            account.TotpSecret = secretProtector?.UnprotectTotpSecret(account.TotpSecret) ?? account.TotpSecret;
        }

        return account;
    }

    private void ProtectTotpSecret(UserAccount account)
    {
        if (!string.IsNullOrWhiteSpace(account.TotpSecret))
        {
            account.TotpSecret = secretProtector?.ProtectTotpSecret(account.TotpSecret) ?? account.TotpSecret;
        }
    }
}
