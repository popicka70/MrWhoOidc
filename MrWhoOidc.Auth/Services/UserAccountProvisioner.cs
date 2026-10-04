using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IUserAccountProvisioner
{
    Task EnsureAsync(User user, Guid tenantId, Guid? defaultRealmId, bool isTenantAdmin, CancellationToken ct = default, bool autoSave = true);

    /// <summary>
    /// Returns the global account that already owns <paramref name="username"/> or <paramref name="email"/>
    /// and is not the account <paramref name="user"/> is linked to; null when there is no conflict.
    /// Per-tenant users are linked to their account by username/email, so a tenant-side write of another
    /// account's identifier would hand that account to the tenant. Call it before mutating <paramref name="user"/>.
    /// </summary>
    Task<UserAccount?> FindConflictingAccountAsync(User? user, string? username, string? email, CancellationToken ct = default);

    /// <summary>
    /// The global account a per-tenant user belongs to: same id for the home user, otherwise matched by
    /// username/email. This is the single place to switch to a foreign key once User carries one.
    /// </summary>
    Task<UserAccount?> FindAccountForUserAsync(User user, CancellationToken ct = default);
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
        bool autoSave = true)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!_options.UserAccountDecouplingEnabled)
        {
            return;
        }

        var normalizedEmail = user.NormalizedEmail ?? EmailNormalizer.NormalizeForLookup(user.Email ?? string.Empty);

        var account = await FindLinkedAccountAsync(user, normalizedEmail, ct).ConfigureAwait(false);

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
            var currentEmail = user.NormalizedEmail ?? EmailNormalizer.NormalizeForLookup(user.Email);
            linkedAccountId = (await FindLinkedAccountAsync(user, currentEmail, ct, track: false).ConfigureAwait(false))?.Id;
        }

        return await dbContext.UserAccounts.AsNoTracking()
            .Where(a => (trimmedUsername != null && a.Username == trimmedUsername)
                        || (normalizedEmail != null && a.NormalizedEmail == normalizedEmail))
            .Where(a => linkedAccountId == null || a.Id != linkedAccountId.Value)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public Task<UserAccount?> FindAccountForUserAsync(User user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var normalizedEmail = user.NormalizedEmail ?? EmailNormalizer.NormalizeForLookup(user.Email);
        return FindLinkedAccountAsync(user, normalizedEmail, ct, track: false);
    }

    private async Task<UserAccount?> FindLinkedAccountAsync(User user, string? normalizedEmail, CancellationToken ct, bool track = true)
    {
        var accounts = track ? dbContext.UserAccounts : dbContext.UserAccounts.AsNoTracking();
        return await accounts.FirstOrDefaultAsync(a => a.Id == user.Id, ct).ConfigureAwait(false)
               ?? await accounts
                   .FirstOrDefaultAsync(a => a.Username == user.Username || (normalizedEmail != null && a.NormalizedEmail == normalizedEmail), ct)
                   .ConfigureAwait(false);
    }
}
