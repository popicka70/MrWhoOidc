using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Admin;

/// <summary>
/// Tests for admin password reset functionality affecting global UserAccount.
/// User Story 5: Admin Password Reset Affects Global Account
/// </summary>
[TestClass]
public sealed class AdminPasswordResetTests
{
    private AuthDbContext _db = null!;
    private IUserAccountService _userAccountService = null!;
    private IPasswordHasher _passwordHasher = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new AuthDbContext(options);
        _userAccountService = new TestUserAccountService(_db);
        _passwordHasher = new TestPasswordHasher();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    [TestMethod]
    public async Task AdminReset_UpdatesUserAccountPassword()
    {
        // Arrange: Create a UserAccount with original password
        var originalHash = _passwordHasher.Hash("OldPassword123!");
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = originalHash,
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Act: Admin resets password to a new one
        var newHash = _passwordHasher.Hash("NewTempPassword!");
        await _userAccountService.UpdatePasswordAsync(
            account.Id,
            newHash,
            null,
            "argon2id");

        // Assert: Password is updated in the database
        var updatedAccount = await _db.UserAccounts.FindAsync(account.Id);
        Assert.IsNotNull(updatedAccount);
        Assert.AreEqual(newHash, updatedAccount.PasswordHash);
        Assert.AreNotEqual(originalHash, updatedAccount.PasswordHash);
    }

    [TestMethod]
    public async Task AdminReset_ClearsLockoutState()
    {
        // Arrange: Create a locked out UserAccount
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "lockeduser",
            Email = "locked@example.com",
            NormalizedEmail = "locked@example.com",
            PasswordHash = _passwordHasher.Hash("OldPassword123!"),
            HashAlgorithm = "argon2id",
            FailedLoginAttempts = 5,
            LastFailedLoginAt = DateTimeOffset.UtcNow,
            LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(15),
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Verify account is locked
        Assert.AreEqual(5, account.FailedLoginAttempts);
        Assert.IsNotNull(account.LockedOutUntil);

        // Act: Admin resets password and clears lockout
        var newHash = _passwordHasher.Hash("NewTempPassword!");
        await _userAccountService.UpdatePasswordAsync(
            account.Id,
            newHash,
            null,
            "argon2id");
        await _userAccountService.UpdateLockoutAsync(
            account.Id,
            failedAttempts: 0,
            lastFailedAt: null,
            lockedOutUntil: null);

        // Assert: Lockout is cleared
        var updatedAccount = await _db.UserAccounts.FindAsync(account.Id);
        Assert.IsNotNull(updatedAccount);
        Assert.AreEqual(0, updatedAccount.FailedLoginAttempts);
        Assert.IsNull(updatedAccount.LastFailedLoginAt);
        Assert.IsNull(updatedAccount.LockedOutUntil);
    }

    [TestMethod]
    public async Task AdminReset_AffectsAllTenantMemberships()
    {
        // Arrange: Create UserAccount with multiple tenant memberships
        var tenant1 = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Tenant 1",
            Slug = "tenant1",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var tenant2 = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Tenant 2",
            Slug = "tenant2",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Tenants.AddRange(tenant1, tenant2);

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "multitenantuser",
            Email = "multi@example.com",
            NormalizedEmail = "multi@example.com",
            PasswordHash = _passwordHasher.Hash("OldPassword123!"),
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);

        var membership1 = new UserTenantMembership
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            TenantId = tenant1.Id,
            IsTenantAdmin = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var membership2 = new UserTenantMembership
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            TenantId = tenant2.Id,
            IsTenantAdmin = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserTenantMemberships.AddRange(membership1, membership2);
        await _db.SaveChangesAsync();

        // Act: Admin resets password via global UserAccount
        var newHash = _passwordHasher.Hash("NewGlobalPassword!");
        await _userAccountService.UpdatePasswordAsync(
            account.Id,
            newHash,
            null,
            "argon2id");

        // Assert: The single global password update applies to all tenants
        var updatedAccount = await _db.UserAccounts.FindAsync(account.Id);
        Assert.IsNotNull(updatedAccount);
        Assert.AreEqual(newHash, updatedAccount.PasswordHash);

        // Verify memberships still exist (password is global, not per-tenant)
        var memberships = await _userAccountService.GetActiveMembershipsAsync(account.Id);
        Assert.AreEqual(2, memberships.Count);
    }

    [TestMethod]
    public async Task AdminReset_SetsPasswordUpdatedTimestamp()
    {
        // Arrange: Create a UserAccount
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "timestampuser",
            Email = "timestamp@example.com",
            NormalizedEmail = "timestamp@example.com",
            PasswordHash = _passwordHasher.Hash("OldPassword123!"),
            HashAlgorithm = "argon2id",
            PasswordUpdatedAt = null, // Never updated
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        var beforeUpdate = DateTimeOffset.UtcNow;

        // Act: Admin resets password
        var newHash = _passwordHasher.Hash("NewTempPassword!");
        await _userAccountService.UpdatePasswordAsync(
            account.Id,
            newHash,
            null,
            "argon2id");

        // Assert: PasswordUpdatedAt is set
        var updatedAccount = await _db.UserAccounts.FindAsync(account.Id);
        Assert.IsNotNull(updatedAccount);
        Assert.IsNotNull(updatedAccount.PasswordUpdatedAt);
        Assert.IsTrue(updatedAccount.PasswordUpdatedAt >= beforeUpdate);
    }

    /// <summary>
    /// Test implementation of IUserAccountService for admin reset tests.
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
    /// Simple password hasher for tests.
    /// </summary>
    private sealed class TestPasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => $"hash:{password}";
        public bool Verify(string password, string hash)
            => hash == $"hash:{password}";
    }
}
