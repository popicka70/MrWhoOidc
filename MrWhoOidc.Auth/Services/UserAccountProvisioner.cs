using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// How <see cref="IUserAccountProvisioner.EnsureAsync"/> may find the global account of a user that is not linked yet.
/// </summary>
public enum AccountLinkMode
{
    /// <summary>
    /// Only the account with the user's own id. An unlinked user whose username or email belongs to another
    /// account is refused with <see cref="AccountLinkConflictException"/>: adopting it handed that account to
    /// whoever created the user (registration, external auto-provisioning, tenant seeding; V3/V4).
    /// Existing people join a tenant through an authenticated invitation, which links by account id.
    /// </summary>
    OwnAccountOnly,

    /// <summary>
    /// Also adopt an account found by username or email. Only for operator-supplied data (startup seeding,
    /// seed manifests), never for anything a tenant admin or an end user controls.
    /// </summary>
    TrustedIdentifierMatch,
}

/// <summary>
/// Thrown when a new per-tenant user would be linked to a global account owned by someone else.
/// </summary>
public sealed class AccountLinkConflictException(Guid accountId)
    : InvalidOperationException("An account with this username or email already exists.")
{
    public Guid AccountId { get; } = accountId;
}

public interface IUserAccountProvisioner
{
    Task EnsureAsync(
        User user,
        Guid tenantId,
        Guid? defaultRealmId,
        bool isTenantAdmin,
        CancellationToken ct = default,
        bool autoSave = true,
        AccountLinkMode linkMode = AccountLinkMode.OwnAccountOnly);

    /// <summary>
    /// Returns the global account that already owns <paramref name="username"/> or <paramref name="email"/>
    /// and is not the account <paramref name="user"/> is linked to; null when there is no conflict.
    /// Legacy per-tenant users without a <c>UserAccountId</c> are still resolved by email, so a tenant-side write
    /// of another account's identifier would hand that account to the tenant. Call it before mutating
    /// <paramref name="user"/> or creating a user.
    /// </summary>
    Task<UserAccount?> FindConflictingAccountAsync(User? user, string? username, string? email, CancellationToken ct = default);
}

internal sealed class UserAccountProvisioner(
    AuthDbContext dbContext,
    IOptions<UserAccountFeatureOptions> featureOptions,
    ILogger<UserAccountProvisioner> logger) : IUserAccountProvisioner
{
    private readonly UserAccountFeatureOptions _options = featureOptions.Value ?? new UserAccountFeatureOptions();

    public async Task EnsureAsync(
        User user,
        Guid tenantId,
        Guid? defaultRealmId,
        bool isTenantAdmin,
        CancellationToken ct = default,
        bool autoSave = true,
        AccountLinkMode linkMode = AccountLinkMode.OwnAccountOnly)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!_options.UserAccountDecouplingEnabled)
        {
            return;
        }

        var normalizedEmail = user.NormalizedEmail ?? EmailNormalizer.NormalizeForLookup(user.Email ?? string.Empty);

        var account = await FindOwnAccountAsync(user, ct).ConfigureAwait(false);

        if (account is null)
        {
            var match = await FindByIdentifiersAsync(user.Username, normalizedEmail, ct).ConfigureAwait(false);
            if (match is not null && linkMode != AccountLinkMode.TrustedIdentifierMatch)
            {
                logger.LogWarning("Refused to link user {UserId} in tenant {TenantId} to existing UserAccount {AccountId} found by username/email",
                    user.Id, tenantId, match.Id);
                throw new AccountLinkConflictException(match.Id);
            }

            account = match;
        }

        if (account is null)
        {
            account = new UserAccount
            {
                Id = user.Id,
                Username = user.Username,
                // Password is managed globally via UserAccountService.UpdatePasswordAsync
                // Not copied from per-tenant User (which no longer has password fields)
                PasswordHash = string.Empty,
                HashAlgorithm = "argon2id",
                Email = user.Email,
                NormalizedEmail = normalizedEmail,
                EmailVerified = user.EmailVerified,
                EmailVerifiedAt = user.EmailVerifiedAt,
                Name = user.Name,
                CreatedAt = user.CreatedAt,
                TotpSecret = user.TotpSecret,
                TotpEnabled = user.TotpEnabled
            };
            dbContext.UserAccounts.Add(account);
            logger.LogDebug("Created UserAccount for user {UserId}", user.Id);
        }
        else if (account.Id != user.Id)
        {
            // The account is owned by another (home) user record. Never copy this tenant's username, email or
            // TOTP onto it: that let a tenant admin rewrite a foreign account and take it over.
            logger.LogWarning("User {UserId} in tenant {TenantId} matched existing UserAccount {AccountId}; account fields left unchanged",
                user.Id, tenantId, account.Id);
        }
        else
        {
            // Keep account in sync with latest profile info (but NOT password - that's managed globally)
            account.Username = user.Username;
            account.Email = user.Email;
            account.NormalizedEmail = normalizedEmail;
            account.EmailVerified = user.EmailVerified;
            account.EmailVerifiedAt = user.EmailVerifiedAt;
            account.Name = user.Name;
            account.TotpSecret = user.TotpSecret;
            account.TotpEnabled = user.TotpEnabled;
        }

        await LinkAsync(user, account.Id, tenantId, ct).ConfigureAwait(false);

        var membershipExists = await dbContext.UserTenantMemberships.AsNoTracking()
            .AnyAsync(m => m.UserAccountId == account.Id && m.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (!membershipExists)
        {
            dbContext.UserTenantMemberships.Add(new UserTenantMembership
            {
                UserAccountId = account.Id,
                TenantId = tenantId,
                DefaultRealmId = defaultRealmId,
                DisplayName = user.Name ?? user.Username,
                IsTenantAdmin = isTenantAdmin,
                Status = TenantMembershipStatus.Active
            });
            logger.LogDebug("Added tenant membership for user {UserId} tenant {TenantId}", user.Id, tenantId);
        }

        if (autoSave)
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<UserAccount?> FindConflictingAccountAsync(User? user, string? username, string? email, CancellationToken ct = default)
    {
        var trimmedUsername = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        var normalizedEmail = EmailNormalizer.NormalizeForLookup(email);
        if (trimmedUsername is null && normalizedEmail is null)
        {
            return null;
        }

        Guid? linkedAccountId = null;
        if (user is not null)
        {
            // The account this user already resolves to: its own, else (legacy, unlinked) the one with its current
            // email. Never by username: an unlinked user sharing a victim's username would exclude the victim's
            // account from the check and could then take its email (K2 residue).
            var currentEmail = user.NormalizedEmail ?? EmailNormalizer.NormalizeForLookup(user.Email);
            linkedAccountId = (await FindOwnAccountAsync(user, ct, track: false).ConfigureAwait(false))?.Id
                ?? (user.UserAccountId is null && currentEmail is not null
                    ? (await dbContext.UserAccounts.AsNoTracking()
                        .FirstOrDefaultAsync(a => a.NormalizedEmail == currentEmail, ct)
                        .ConfigureAwait(false))?.Id
                    : null);
        }

        return await dbContext.UserAccounts.AsNoTracking()
            .Where(a => (trimmedUsername != null && a.Username == trimmedUsername)
                        || (normalizedEmail != null && a.NormalizedEmail == normalizedEmail))
            .Where(a => linkedAccountId == null || a.Id != linkedAccountId.Value)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<UserAccount?> FindOwnAccountAsync(User user, CancellationToken ct, bool track = true)
    {
        var accounts = track ? dbContext.UserAccounts : dbContext.UserAccounts.AsNoTracking();
        // The foreign key is authoritative: never re-match a linked user by username/email.
        var accountId = user.UserAccountId ?? user.Id;
        return await accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false);
    }

    private Task<UserAccount?> FindByIdentifiersAsync(string? username, string? normalizedEmail, CancellationToken ct)
    {
        username = string.IsNullOrWhiteSpace(username) ? null : username;
        normalizedEmail = string.IsNullOrWhiteSpace(normalizedEmail) ? null : normalizedEmail;
        return dbContext.UserAccounts
            .FirstOrDefaultAsync(a => (username != null && a.Username == username) || (normalizedEmail != null && a.NormalizedEmail == normalizedEmail), ct);
    }

    private async Task LinkAsync(User user, Guid accountId, Guid tenantId, CancellationToken ct)
    {
        if (user.UserAccountId is not null)
        {
            return;
        }

        // One user per account per tenant (unique index): leave legacy duplicates unlinked rather than fail.
        var alreadyLinked = await dbContext.Users.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(u => u.TenantId == tenantId && u.UserAccountId == accountId && u.Id != user.Id, ct)
            .ConfigureAwait(false);
        if (alreadyLinked)
        {
            logger.LogWarning("UserAccount {AccountId} is already linked to another user in tenant {TenantId}; user {UserId} left unlinked",
                accountId, tenantId, user.Id);
            return;
        }

        user.UserAccountId = accountId;
        if (dbContext.Entry(user).State == EntityState.Detached)
        {
            dbContext.Users.Attach(user);
            dbContext.Entry(user).Property(u => u.UserAccountId).IsModified = true;
        }
    }
}
