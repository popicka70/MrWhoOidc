using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Integration;

/// <summary>
/// Integration tests for password migration preserving user access.
/// User Story 6: Migration of Existing Users
/// </summary>
[TestClass]
public sealed class PasswordMigrationIntegrationTests
{
    private AuthDbContext _db = null!;
    private IPasswordMigrationService _migrationService = null!;
    private IGlobalAuthenticationService _authService = null!;
    private IPasswordHasher _passwordHasher = null!;
    private IUserAccountService _userAccountService = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new AuthDbContext(options);
        _passwordHasher = new TestPasswordHasher();
        _userAccountService = new TestUserAccountService(_db);
        _migrationService = new PasswordMigrationService(_db);
        _authService = new TestGlobalAuthenticationService(_db, _passwordHasher, _userAccountService);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    [TestMethod]
    public async Task Migration_PreservesUserAccess_AfterMigration()
    {
        // Arrange: Create existing per-tenant user with password
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Test Org",
            Slug = "test-org",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Tenants.Add(tenant);

        var originalPassword = "OriginalPassword123!";
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Username = "legacyuser",
            Email = "legacy@example.com",
            NormalizedEmail = "legacy@example.com",
            PasswordHash = _passwordHasher.Hash(originalPassword),
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Users.Add(user);

        // Create UserAccount without password (pre-migration state)
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "legacyuser",
            Email = "legacy@example.com",
            NormalizedEmail = "legacy@example.com",
            PasswordHash = string.Empty, // No password yet
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);

        _db.UserTenantMemberships.Add(new UserTenantMembership
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            TenantId = tenant.Id,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        // Verify: Before migration, user cannot authenticate via global service
        var preMigrationAuth = await _authService.AuthenticateAsync(account.Email, originalPassword);
        Assert.IsFalse(preMigrationAuth.Succeeded);

        // Act: Run migration
        var migrationResult = await _migrationService.MigrateUserCredentialsAsync(account.Id);
        Assert.IsTrue(migrationResult.Success);

        // Assert: After migration, user CAN authenticate with original password
        var postMigrationAuth = await _authService.AuthenticateAsync(account.Email, originalPassword);
        Assert.IsTrue(postMigrationAuth.Succeeded);
        Assert.AreEqual(account.Id, postMigrationAuth.Account!.Id);
    }

    [TestMethod]
    public async Task Migration_BatchProcess_MigratesAllPendingAccounts()
    {
        // Arrange: Create multiple users needing migration
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Batch Org",
            Slug = "batch-org",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Tenants.Add(tenant);

        var password1 = "Password1!";
        var password2 = "Password2!";
        var password3 = "Password3!";

        // Create users
        var user1 = CreateUser(tenant.Id, "user1", "user1@example.com", password1);
        var user2 = CreateUser(tenant.Id, "user2", "user2@example.com", password2);
        var user3 = CreateUser(tenant.Id, "user3", "user3@example.com", password3);
        _db.Users.AddRange(user1, user2, user3);

        // Create accounts without passwords
        var account1 = CreateAccountWithoutPassword("user1", "user1@example.com");
        var account2 = CreateAccountWithoutPassword("user2", "user2@example.com");
        var account3 = CreateAccountWithoutPassword("user3", "user3@example.com");
        _db.UserAccounts.AddRange(account1, account2, account3);

        // Link with memberships
        _db.UserTenantMemberships.AddRange(
            CreateMembership(account1.Id, tenant.Id),
            CreateMembership(account2.Id, tenant.Id),
            CreateMembership(account3.Id, tenant.Id)
        );
        await _db.SaveChangesAsync();

        // Act: Batch migrate all
        var batchResult = await _migrationService.MigrateBatchAsync();

        // Assert
        Assert.AreEqual(3, batchResult.ProcessedCount);
        Assert.AreEqual(3, batchResult.SuccessCount);
        Assert.AreEqual(0, batchResult.FailureCount);

        // Verify all can authenticate
        Assert.IsTrue((await _authService.AuthenticateAsync("user1@example.com", password1)).Succeeded);
        Assert.IsTrue((await _authService.AuthenticateAsync("user2@example.com", password2)).Succeeded);
        Assert.IsTrue((await _authService.AuthenticateAsync("user3@example.com", password3)).Succeeded);
    }

    [TestMethod]
    public async Task Migration_MultiTenantUser_SelectsMostRecentPassword()
    {
        // Arrange: User exists in multiple tenants with different passwords
        var tenant1 = new Tenant { Id = Guid.NewGuid(), Name = "Org 1", Slug = "org1", CreatedAt = DateTimeOffset.UtcNow };
        var tenant2 = new Tenant { Id = Guid.NewGuid(), Name = "Org 2", Slug = "org2", CreatedAt = DateTimeOffset.UtcNow };
        _db.Tenants.AddRange(tenant1, tenant2);

        // Older password in tenant 1
        var oldPassword = "OldPassword!";
        var user1 = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant1.Id,
            Username = "multiuser",
            Email = "multi@example.com",
            NormalizedEmail = "multi@example.com",
            PasswordHash = _passwordHasher.Hash(oldPassword),
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30) // Older
        };

        // Newer password in tenant 2
        var newPassword = "NewPassword!";
        var user2 = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant2.Id,
            Username = "multiuser",
            Email = "multi@example.com",
            NormalizedEmail = "multi@example.com",
            PasswordHash = _passwordHasher.Hash(newPassword),
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow // Newer
        };
        _db.Users.AddRange(user1, user2);

        var account = CreateAccountWithoutPassword("multiuser", "multi@example.com");
        _db.UserAccounts.Add(account);

        _db.UserTenantMemberships.AddRange(
            CreateMembership(account.Id, tenant1.Id),
            CreateMembership(account.Id, tenant2.Id)
        );
        await _db.SaveChangesAsync();

        // Act: Migrate
        var result = await _migrationService.MigrateUserCredentialsAsync(account.Id);
        Assert.IsTrue(result.Success);

        // Assert: New password works, old password doesn't
        var authWithNew = await _authService.AuthenticateAsync("multi@example.com", newPassword);
        var authWithOld = await _authService.AuthenticateAsync("multi@example.com", oldPassword);

        Assert.IsTrue(authWithNew.Succeeded, "New password should work");
        Assert.IsFalse(authWithOld.Succeeded, "Old password should NOT work");
    }

    #region Helpers

    private User CreateUser(Guid tenantId, string username, string email, string password)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Username = username,
            Email = email,
            NormalizedEmail = email.ToLowerInvariant(),
            PasswordHash = _passwordHasher.Hash(password),
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private UserAccount CreateAccountWithoutPassword(string username, string email)
    {
        return new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            NormalizedEmail = email.ToLowerInvariant(),
            PasswordHash = string.Empty,
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private UserTenantMembership CreateMembership(Guid accountId, Guid tenantId)
    {
        return new UserTenantMembership
        {
            Id = Guid.NewGuid(),
            UserAccountId = accountId,
            TenantId = tenantId,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    #endregion

    #region Test Implementations

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

        public async Task UpdatePasswordAsync(Guid accountId, string newPasswordHash, string? salt, string algorithm, CancellationToken ct = default)
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

        public async Task<IReadOnlyList<UserTenantMembership>> GetActiveMembershipsAsync(Guid accountId, CancellationToken ct = default)
        {
            return await db.UserTenantMemberships
                .Where(m => m.UserAccountId == accountId && m.Status == TenantMembershipStatus.Active)
                .ToListAsync(ct);
        }

        public async Task UpdateLockoutAsync(Guid accountId, int failedAttempts, DateTimeOffset? lastFailedAt, DateTimeOffset? lockedOutUntil, CancellationToken ct = default)
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

        public Task EnableMfaAsync(Guid accountId, string totpSecret, CancellationToken ct = default) => Task.CompletedTask;
        public Task ConfirmMfaAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisableMfaAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(bool Enabled, string? Secret)> GetMfaStatusAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult((false, (string?)null));
    }

    private sealed class TestGlobalAuthenticationService(
        AuthDbContext db,
        IPasswordHasher passwordHasher,
        IUserAccountService userAccountService) : IGlobalAuthenticationService
    {
        public async Task<GlobalAuthenticationResult> AuthenticateAsync(string usernameOrEmail, string password, CancellationToken ct = default)
        {
            var account = await userAccountService.FindByUsernameOrEmailAsync(usernameOrEmail, ct);
            if (account is null)
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.UserNotFound);

            if (account.LockedOutUntil.HasValue && account.LockedOutUntil > DateTimeOffset.UtcNow)
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.AccountLocked, account.LockedOutUntil);

            if (string.IsNullOrEmpty(account.PasswordHash) || !passwordHasher.Verify(password, account.PasswordHash))
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.InvalidPassword);

            var memberships = await userAccountService.GetActiveMembershipsAsync(account.Id, ct);
            if (memberships.Count == 0)
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.NoActiveMemberships);

            if (account.TotpEnabled)
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.MfaRequired);

            return GlobalAuthenticationResult.Success(account, memberships);
        }

        public Task RecordFailedAttemptAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearFailedAttemptsAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsLockedOutAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<UserAccount?> FindAccountByEmailAsync(string email, CancellationToken ct = default)
            => userAccountService.FindByEmailAsync(email, ct);
    }

    private sealed class TestPasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => $"hash:{password}";
        public bool Verify(string password, string hash) => hash == $"hash:{password}";
    }

    #endregion
}
