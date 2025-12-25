using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Integration;

/// <summary>
/// Integration tests for admin password reset affecting global UserAccount.
/// User Story 5: Admin Password Reset Affects Global Account
/// </summary>
[TestClass]
public sealed class AdminPasswordResetIntegrationTests
{
    private AuthDbContext _db = null!;
    private IUserAccountService _userAccountService = null!;
    private IPasswordHasher _passwordHasher = null!;
    private IGlobalAuthenticationService _authService = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new AuthDbContext(options);
        _passwordHasher = new TestPasswordHasher();
        _userAccountService = new TestUserAccountService(_db);
        _authService = new TestGlobalAuthenticationService(_db, _passwordHasher, _userAccountService);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    [TestMethod]
    public async Task AdminReset_AffectsAllTenants_UserCanLoginWithNewPassword()
    {
        // Arrange: Create tenants and user with memberships
        var tenant1 = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Org One",
            Slug = "org-one",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var tenant2 = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Org Two",
            Slug = "org-two",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Tenants.AddRange(tenant1, tenant2);

        var originalPassword = "Original123!";
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "admin.reset.user",
            Email = "admin.reset@example.com",
            NormalizedEmail = "admin.reset@example.com",
            PasswordHash = _passwordHasher.Hash(originalPassword),
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);

        _db.UserTenantMemberships.AddRange(
            new UserTenantMembership
            {
                Id = Guid.NewGuid(),
                UserAccountId = account.Id,
                TenantId = tenant1.Id,
                IsTenantAdmin = false,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new UserTenantMembership
            {
                Id = Guid.NewGuid(),
                UserAccountId = account.Id,
                TenantId = tenant2.Id,
                IsTenantAdmin = true,
                CreatedAt = DateTimeOffset.UtcNow
            }
        );
        await _db.SaveChangesAsync();

        // Verify original password works
        var originalResult = await _authService.AuthenticateAsync(account.Email, originalPassword);
        Assert.IsTrue(originalResult.Succeeded);

        // Act: Admin resets password globally
        var newPassword = "AdminReset456!";
        await _userAccountService.UpdatePasswordAsync(
            account.Id,
            _passwordHasher.Hash(newPassword),
            null,
            "argon2id");
        await _userAccountService.UpdateLockoutAsync(
            account.Id,
            failedAttempts: 0,
            lastFailedAt: null,
            lockedOutUntil: null);

        // Assert: Old password no longer works
        var oldPasswordResult = await _authService.AuthenticateAsync(account.Email, originalPassword);
        Assert.IsFalse(oldPasswordResult.Succeeded);
        Assert.AreEqual(AuthenticationFailureReason.InvalidPassword, oldPasswordResult.FailureReason);

        // Assert: New password works for both tenants
        var newPasswordResult = await _authService.AuthenticateAsync(account.Email, newPassword);
        Assert.IsTrue(newPasswordResult.Succeeded);
        Assert.AreEqual(account.Id, newPasswordResult.Account!.Id);

        // Verify user still has both tenant memberships
        var memberships = await _userAccountService.GetActiveMembershipsAsync(account.Id);
        Assert.AreEqual(2, memberships.Count);
    }

    [TestMethod]
    public async Task AdminReset_UnlocksAccount_UserCanLoginImmediately()
    {
        // Arrange: Create a locked-out user
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Test Org",
            Slug = "test-org",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Tenants.Add(tenant);

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "locked.user",
            Email = "locked.user@example.com",
            NormalizedEmail = "locked.user@example.com",
            PasswordHash = _passwordHasher.Hash("OldPassword123!"),
            HashAlgorithm = "argon2id",
            FailedLoginAttempts = 5,
            LastFailedLoginAt = DateTimeOffset.UtcNow,
            LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(15),
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);

        _db.UserTenantMemberships.Add(new UserTenantMembership
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            TenantId = tenant.Id,
            IsTenantAdmin = false,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        // Verify account is locked out
        var lockedResult = await _authService.AuthenticateAsync(account.Email, "OldPassword123!");
        Assert.IsFalse(lockedResult.Succeeded);
        Assert.AreEqual(AuthenticationFailureReason.AccountLocked, lockedResult.FailureReason);

        // Act: Admin resets password and clears lockout
        var newPassword = "UnlockedPass789!";
        await _userAccountService.UpdatePasswordAsync(
            account.Id,
            _passwordHasher.Hash(newPassword),
            null,
            "argon2id");
        await _userAccountService.UpdateLockoutAsync(
            account.Id,
            failedAttempts: 0,
            lastFailedAt: null,
            lockedOutUntil: null);

        // Assert: User can now login immediately
        var unlockedResult = await _authService.AuthenticateAsync(account.Email, newPassword);
        Assert.IsTrue(unlockedResult.Succeeded);
    }

    [TestMethod]
    public async Task AdminReset_PreservesMfaSettings()
    {
        // Arrange: Create a user with MFA enabled
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "MFA Org",
            Slug = "mfa-org",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Tenants.Add(tenant);

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "mfa.user",
            Email = "mfa.user@example.com",
            NormalizedEmail = "mfa.user@example.com",
            PasswordHash = _passwordHasher.Hash("MfaPassword123!"),
            HashAlgorithm = "argon2id",
            TotpEnabled = true,
            TotpSecret = "JBSWY3DPEHPK3PXP",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);

        _db.UserTenantMemberships.Add(new UserTenantMembership
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            TenantId = tenant.Id,
            IsTenantAdmin = false,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        // Act: Admin resets password only (not MFA)
        var newPassword = "NewMfaPass456!";
        await _userAccountService.UpdatePasswordAsync(
            account.Id,
            _passwordHasher.Hash(newPassword),
            null,
            "argon2id");

        // Assert: MFA settings are preserved
        var updatedAccount = await _db.UserAccounts.FindAsync(account.Id);
        Assert.IsNotNull(updatedAccount);
        Assert.IsTrue(updatedAccount.TotpEnabled);
        Assert.AreEqual("JBSWY3DPEHPK3PXP", updatedAccount.TotpSecret);

        // Auth result should indicate MFA is required
        var authResult = await _authService.AuthenticateAsync(account.Email, newPassword);
        Assert.IsFalse(authResult.Succeeded);
        Assert.AreEqual(AuthenticationFailureReason.MfaRequired, authResult.FailureReason);
    }

    /// <summary>
    /// Test implementation of IUserAccountService.
    /// </summary>
    private sealed class TestUserAccountService(AuthDbContext db) : IUserAccountService
    {
        public Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => db.UserAccounts.FindAsync([id], ct).AsTask();

        public Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default)
            => db.UserAccounts.FirstOrDefaultAsync(x => x.Username == username, ct);

        public async Task<UserAccount> CreateAsync(UserAccount account, CancellationToken ct = default)
        {
            db.UserAccounts.Add(account);
            await db.SaveChangesAsync(ct);
            return account;
        }

        public Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default)
        {
            var normalized = email.ToLowerInvariant();
            return db.UserAccounts.FirstOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);
        }

        public Task<UserAccount?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default)
        {
            var normalized = usernameOrEmail.ToLowerInvariant();
            return db.UserAccounts.FirstOrDefaultAsync(
                x => x.Username == usernameOrEmail || x.NormalizedEmail == normalized, ct);
        }

        public async Task UpdatePasswordAsync(
            Guid accountId,
            string newPasswordHash,
            string? salt,
            string algorithm,
            CancellationToken ct = default)
        {
            var account = await db.UserAccounts.FindAsync([accountId], ct);
            if (account is not null)
            {
                account.PasswordHash = newPasswordHash;
                account.HashAlgorithm = algorithm;
                account.PasswordUpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }

        public async Task<IReadOnlyList<UserTenantMembership>> GetActiveMembershipsAsync(
            Guid accountId,
            CancellationToken ct = default)
        {
            return await db.UserTenantMemberships
                .Where(m => m.UserAccountId == accountId && m.Status == TenantMembershipStatus.Active)
                .ToListAsync(ct);
        }

        public async Task UpdateLockoutAsync(
            Guid accountId,
            int failedAttempts,
            DateTimeOffset? lastFailedAt,
            DateTimeOffset? lockedOutUntil,
            CancellationToken ct = default)
        {
            var account = await db.UserAccounts.FindAsync([accountId], ct);
            if (account is not null)
            {
                account.FailedLoginAttempts = failedAttempts;
                account.LastFailedLoginAt = lastFailedAt;
                account.LockedOutUntil = lockedOutUntil;
                await db.SaveChangesAsync(ct);
            }
        }

        public Task EnableMfaAsync(Guid accountId, string totpSecret, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ConfirmMfaAsync(Guid accountId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task DisableMfaAsync(Guid accountId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<(bool Enabled, string? Secret)> GetMfaStatusAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult((false, (string?)null));
    }

    /// <summary>
    /// Test implementation of IGlobalAuthenticationService.
    /// </summary>
    private sealed class TestGlobalAuthenticationService(
#pragma warning disable CS9113 // Parameter 'db' is unread - kept for DI compatibility
        AuthDbContext db,
#pragma warning restore CS9113
        IPasswordHasher passwordHasher,
        IUserAccountService userAccountService) : IGlobalAuthenticationService
    {
        public async Task<GlobalAuthenticationResult> AuthenticateAsync(
            string usernameOrEmail,
            string password,
            CancellationToken ct = default)
        {
            var account = await userAccountService.FindByUsernameOrEmailAsync(usernameOrEmail, ct);
            if (account is null)
            {
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.UserNotFound);
            }

            // Check lockout
            if (account.LockedOutUntil.HasValue && account.LockedOutUntil > DateTimeOffset.UtcNow)
            {
                return GlobalAuthenticationResult.Failure(
                    AuthenticationFailureReason.AccountLocked,
                    account.LockedOutUntil);
            }

            // Verify password
            if (!passwordHasher.Verify(password, account.PasswordHash))
            {
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.InvalidPassword);
            }

            // Check for active memberships
            var memberships = await userAccountService.GetActiveMembershipsAsync(account.Id, ct);
            if (memberships.Count == 0)
            {
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.NoActiveMemberships);
            }

            // Check MFA
            if (account.TotpEnabled)
            {
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.MfaRequired);
            }

            return GlobalAuthenticationResult.Success(account, memberships);
        }

        public Task RecordFailedAttemptAsync(Guid accountId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ClearFailedAttemptsAsync(Guid accountId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> IsLockedOutAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<UserAccount?> FindAccountByEmailAsync(string email, CancellationToken ct = default)
            => userAccountService.FindByEmailAsync(email, ct);
    }

    /// <summary>
    /// Simple password hasher for tests.
    /// </summary>
    private sealed class TestPasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => $"hash:{password}";
        public bool Verify(string password, string hash)
            => hash == $"hash:{password}";
    }
}
