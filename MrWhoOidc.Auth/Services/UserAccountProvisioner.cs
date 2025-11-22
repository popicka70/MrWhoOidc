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

        var account = await dbContext.UserAccounts
            .FirstOrDefaultAsync(a => a.Id == user.Id, ct)
            .ConfigureAwait(false);

        if (account is null)
        {
            account = await dbContext.UserAccounts
                .FirstOrDefaultAsync(a => a.Username == user.Username || (normalizedEmail != null && a.NormalizedEmail == normalizedEmail), ct)
                .ConfigureAwait(false);
        }

        if (account is null)
        {
            account = new UserAccount
            {
                Id = user.Id,
                Username = user.Username,
                PasswordHash = user.PasswordHash,
                PasswordSalt = user.PasswordSalt,
                HashAlgorithm = user.HashAlgorithm,
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
            logger.LogDebug("Created UserAccount for legacy user {UserId}", user.Id);
        }
        else
        {
            // Keep account in sync with latest profile info.
            account.Username = user.Username;
            account.PasswordHash = user.PasswordHash;
            account.PasswordSalt = user.PasswordSalt;
            account.HashAlgorithm = user.HashAlgorithm;
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
}
