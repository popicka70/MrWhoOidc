using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Integration;

/// <summary>
/// Integration tests for password change functionality with global credentials.
/// Verifies that password changes propagate correctly across all tenants.
/// </summary>
[TestClass]
public class PasswordChangeIntegrationTests
{
    private AuthDbContext _db = null!;
    private IPasswordHasher _hasher = null!;
    private IUserAccountService _userAccountService = null!;
    private IGlobalAuthenticationService _globalAuthService = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase($"PasswordChangeIntegrationTests_{Guid.NewGuid()}")
            .Options;

        _db = new AuthDbContext(options);
        _hasher = new Argon2PasswordHasher();
        _userAccountService = new UserAccountServiceInternal(_db);
        _globalAuthService = new GlobalAuthServiceInternal(_userAccountService, _hasher);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    [TestMethod]
    public async Task PasswordChange_PropagatesAcrossAllTenants()
    {
        // Arrange: Create a user account with memberships in multiple tenants
        var tenant1 = new Tenant { Id = Guid.NewGuid(), Name = "Tenant One", Slug = "tenant1" };
        var tenant2 = new Tenant { Id = Guid.NewGuid(), Name = "Tenant Two", Slug = "tenant2" };
        var tenant3 = new Tenant { Id = Guid.NewGuid(), Name = "Tenant Three", Slug = "tenant3" };
        
        _db.Tenants.AddRange(tenant1, tenant2, tenant3);

        var originalPassword = "OriginalPassword123!";
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "multitenantuser",
            Email = "multi@example.com",
            NormalizedEmail = "multi@example.com",
            PasswordHash = _hasher.Hash(originalPassword),
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);

        // Add memberships to all three tenants
        _db.UserTenantMemberships.AddRange(
            new UserTenantMembership { Id = Guid.NewGuid(), UserAccountId = account.Id, TenantId = tenant1.Id, Status = TenantMembershipStatus.Active },
            new UserTenantMembership { Id = Guid.NewGuid(), UserAccountId = account.Id, TenantId = tenant2.Id, Status = TenantMembershipStatus.Active },
            new UserTenantMembership { Id = Guid.NewGuid(), UserAccountId = account.Id, TenantId = tenant3.Id, Status = TenantMembershipStatus.Active }
        );
        await _db.SaveChangesAsync();

        // Verify original password works
        var authBefore = await _globalAuthService.AuthenticateAsync(account.Username, originalPassword);
        Assert.IsTrue(authBefore.Succeeded, "Original password should work before change");

        // Act: Change the password
        var newPassword = "NewSecurePassword456!";
        var newHash = _hasher.Hash(newPassword);
        await _userAccountService.UpdatePasswordAsync(account.Id, newHash, null, "argon2id");

        // Assert: Old password no longer works
        var authOldPwd = await _globalAuthService.AuthenticateAsync(account.Username, originalPassword);
        Assert.IsFalse(authOldPwd.Succeeded, "Old password should not work after change");
        Assert.AreEqual(AuthenticationFailureReason.InvalidPassword, authOldPwd.FailureReason);

        // Assert: New password works
        var authNewPwd = await _globalAuthService.AuthenticateAsync(account.Username, newPassword);
        Assert.IsTrue(authNewPwd.Succeeded, "New password should work after change");

        // Assert: All tenant memberships are still accessible
        Assert.AreEqual(3, authNewPwd.Memberships.Count, "All three tenant memberships should still be accessible");
    }

    [TestMethod]
    public async Task PasswordChange_ClearsExistingLockout()
    {
        // Arrange: Create a locked-out account
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Test Tenant", Slug = "test" };
        _db.Tenants.Add(tenant);

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "lockeduser",
            Email = "locked@example.com",
            NormalizedEmail = "locked@example.com",
            PasswordHash = _hasher.Hash("OldPassword123!"),
            HashAlgorithm = "argon2id",
            FailedLoginAttempts = 5,
            LastFailedLoginAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(5) // Locked for 5 more minutes
        };
        _db.UserAccounts.Add(account);
        _db.UserTenantMemberships.Add(new UserTenantMembership 
        { 
            Id = Guid.NewGuid(), 
            UserAccountId = account.Id, 
            TenantId = tenant.Id, 
            Status = TenantMembershipStatus.Active 
        });
        await _db.SaveChangesAsync();

        // Verify the account is locked out
        var lockedAuth = await _globalAuthService.AuthenticateAsync(account.Username, "OldPassword123!");
        Assert.IsFalse(lockedAuth.Succeeded, "Account should be locked");
        Assert.AreEqual(AuthenticationFailureReason.AccountLocked, lockedAuth.FailureReason);

        // Act: Change the password (this simulates admin reset or user reset via email)
        var newPassword = "NewPassword789!";
        var newHash = _hasher.Hash(newPassword);
        await _userAccountService.UpdatePasswordAsync(account.Id, newHash, null, "argon2id");

        // Assert: Lockout is cleared and new password works immediately
        var authAfter = await _globalAuthService.AuthenticateAsync(account.Username, newPassword);
        Assert.IsTrue(authAfter.Succeeded, "New password should work immediately after reset");
    }

    [TestMethod]
    public async Task PasswordChange_UpdatesPasswordUpdatedAtTimestamp()
    {
        // Arrange
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Test", Slug = "test" };
        _db.Tenants.Add(tenant);

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "timestampuser",
            Email = "timestamp@example.com",
            NormalizedEmail = "timestamp@example.com",
            PasswordHash = _hasher.Hash("OldPassword123!"),
            HashAlgorithm = "argon2id",
            PasswordUpdatedAt = null // Never updated before
        };
        _db.UserAccounts.Add(account);
        _db.UserTenantMemberships.Add(new UserTenantMembership 
        { 
            Id = Guid.NewGuid(), 
            UserAccountId = account.Id, 
            TenantId = tenant.Id, 
            Status = TenantMembershipStatus.Active 
        });
        await _db.SaveChangesAsync();

        var beforeChange = DateTimeOffset.UtcNow;

        // Act
        var newHash = _hasher.Hash("NewPassword456!");
        await _userAccountService.UpdatePasswordAsync(account.Id, newHash, null, "argon2id");

        // Assert
        var updated = await _db.UserAccounts.FirstAsync(a => a.Id == account.Id);
        Assert.IsNotNull(updated.PasswordUpdatedAt);
        Assert.IsTrue(updated.PasswordUpdatedAt >= beforeChange);
        Assert.IsTrue(updated.PasswordUpdatedAt <= DateTimeOffset.UtcNow);
    }

    #region Test Helpers

    /// <summary>
    /// Internal implementation of IUserAccountService for testing.
    /// </summary>
    private sealed class UserAccountServiceInternal(AuthDbContext db) : IUserAccountService
    {
        public async Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => await db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

        public async Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            return await db.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Username == username, ct);
        }

        public async Task<UserAccount> CreateAsync(UserAccount account, CancellationToken ct = default)
        {
            db.UserAccounts.Add(account);
            await db.SaveChangesAsync(ct);
            return account;
        }

        public async Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            var normalized = email.Trim().ToLowerInvariant();
            return await db.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);
        }

        public async Task<UserAccount?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(usernameOrEmail)) return null;
            var trimmed = usernameOrEmail.Trim();
            var normalized = trimmed.ToLowerInvariant();
            return await db.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Username == trimmed || x.NormalizedEmail == normalized, ct);
        }

        public async Task UpdatePasswordAsync(Guid accountId, string newPasswordHash, string? salt, string algorithm, CancellationToken ct = default)
        {
            var account = await db.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");

            account.PasswordHash = newPasswordHash;
            account.PasswordSalt = salt;
            account.HashAlgorithm = algorithm;
            account.PasswordUpdatedAt = DateTimeOffset.UtcNow;
            account.FailedLoginAttempts = 0;
            account.LastFailedLoginAt = null;
            account.LockedOutUntil = null;

            await db.SaveChangesAsync(ct);
        }

        public async Task<IReadOnlyList<UserTenantMembership>> GetActiveMembershipsAsync(Guid accountId, CancellationToken ct = default)
        {
            return await db.UserTenantMemberships
                .AsNoTracking()
                .Include(x => x.Tenant)
                .Where(x => x.UserAccountId == accountId
                    && x.Status == TenantMembershipStatus.Active
                    && (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow))
                .ToListAsync(ct);
        }

        public async Task UpdateLockoutAsync(Guid accountId, int failedAttempts, DateTimeOffset? lastFailedAt, DateTimeOffset? lockedOutUntil, CancellationToken ct = default)
        {
            var account = await db.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");

            account.FailedLoginAttempts = failedAttempts;
            account.LastFailedLoginAt = lastFailedAt;
            account.LockedOutUntil = lockedOutUntil;

            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Internal implementation of IGlobalAuthenticationService for testing.
    /// </summary>
    private sealed class GlobalAuthServiceInternal(IUserAccountService userAccountService, IPasswordHasher hasher) : IGlobalAuthenticationService
    {
        private const int MaxFailedAttempts = 5;
        private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

        public async Task<GlobalAuthenticationResult> AuthenticateAsync(string usernameOrEmail, string password, CancellationToken ct = default)
        {
            var account = await userAccountService.FindByUsernameOrEmailAsync(usernameOrEmail, ct);
            if (account is null)
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.UserNotFound);

            if (await IsLockedOutAsync(account.Id, ct))
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.AccountLocked, account.LockedOutUntil);

            if (!hasher.Verify(password, account.PasswordHash))
            {
                await RecordFailedAttemptAsync(account.Id, ct);
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.InvalidPassword);
            }

            var memberships = await userAccountService.GetActiveMembershipsAsync(account.Id, ct);
            if (memberships.Count == 0)
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.NoActiveMemberships);

            await ClearFailedAttemptsAsync(account.Id, ct);
            return GlobalAuthenticationResult.Success(account, memberships);
        }

        public async Task RecordFailedAttemptAsync(Guid accountId, CancellationToken ct = default)
        {
            var account = await userAccountService.GetByIdAsync(accountId, ct);
            if (account is null) return;

            var failedAttempts = account.FailedLoginAttempts + 1;
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? lockedOutUntil = failedAttempts >= MaxFailedAttempts ? now.Add(LockoutDuration) : null;

            await userAccountService.UpdateLockoutAsync(accountId, failedAttempts, now, lockedOutUntil, ct);
        }

        public async Task ClearFailedAttemptsAsync(Guid accountId, CancellationToken ct = default)
        {
            await userAccountService.UpdateLockoutAsync(accountId, 0, null, null, ct);
        }

        public async Task<bool> IsLockedOutAsync(Guid accountId, CancellationToken ct = default)
        {
            var account = await userAccountService.GetByIdAsync(accountId, ct);
            return account?.LockedOutUntil.HasValue == true && account.LockedOutUntil.Value > DateTimeOffset.UtcNow;
        }

        public async Task<UserAccount?> FindAccountByEmailAsync(string email, CancellationToken ct = default)
        {
            return await userAccountService.FindByEmailAsync(email, ct);
        }
    }

    #endregion
}
