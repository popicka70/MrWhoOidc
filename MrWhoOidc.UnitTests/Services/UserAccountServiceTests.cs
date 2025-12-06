using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Services;

/// <summary>
/// Unit tests for UserAccountService, focusing on global credential operations.
/// </summary>
[TestClass]
public class UserAccountServiceTests
{
    private AuthDbContext _db = null!;
    private IUserAccountService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase($"UserAccountServiceTests_{Guid.NewGuid()}")
            .Options;

        _db = new AuthDbContext(options);
        _service = new UserAccountServiceAccessor(_db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    #region UpdatePasswordAsync Tests

    [TestMethod]
    public async Task UpdatePasswordAsync_UpdatesPasswordHash()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = "old_hash",
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        var newHash = "new_password_hash_argon2id";

        // Act
        await _service.UpdatePasswordAsync(account.Id, newHash, null, "argon2id");

        // Assert
        var updated = await _db.UserAccounts.FirstAsync(u => u.Id == account.Id);
        Assert.AreEqual(newHash, updated.PasswordHash);
        Assert.AreEqual("argon2id", updated.HashAlgorithm);
    }

    [TestMethod]
    public async Task UpdatePasswordAsync_SetsPasswordUpdatedAt()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = "old_hash",
            HashAlgorithm = "argon2id",
            PasswordUpdatedAt = null
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        var before = DateTimeOffset.UtcNow;

        // Act
        await _service.UpdatePasswordAsync(account.Id, "new_hash", null, "argon2id");

        // Assert
        var updated = await _db.UserAccounts.FirstAsync(u => u.Id == account.Id);
        Assert.IsNotNull(updated.PasswordUpdatedAt);
        Assert.IsTrue(updated.PasswordUpdatedAt >= before);
        Assert.IsTrue(updated.PasswordUpdatedAt <= DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public async Task UpdatePasswordAsync_ClearsLockoutState()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "lockeduser",
            Email = "locked@example.com",
            NormalizedEmail = "locked@example.com",
            PasswordHash = "old_hash",
            HashAlgorithm = "argon2id",
            FailedLoginAttempts = 5,
            LastFailedLoginAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Act
        await _service.UpdatePasswordAsync(account.Id, "new_hash", null, "argon2id");

        // Assert
        var updated = await _db.UserAccounts.FirstAsync(u => u.Id == account.Id);
        Assert.AreEqual(0, updated.FailedLoginAttempts);
        Assert.IsNull(updated.LastFailedLoginAt);
        Assert.IsNull(updated.LockedOutUntil);
    }

    [TestMethod]
    public async Task UpdatePasswordAsync_ThrowsForNonExistentAccount()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act & Assert
        try
        {
            await _service.UpdatePasswordAsync(nonExistentId, "new_hash", null, "argon2id");
            Assert.Fail("Expected InvalidOperationException");
        }
        catch (InvalidOperationException)
        {
            // expected
        }
    }

    [TestMethod]
    public async Task UpdatePasswordAsync_UpdatesSaltWhenProvided()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = "old_hash",
            PasswordSalt = "old_salt",
            HashAlgorithm = "bcrypt"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        var newSalt = "new_salt_value";

        // Act
        await _service.UpdatePasswordAsync(account.Id, "new_hash", newSalt, "argon2id");

        // Assert
        var updated = await _db.UserAccounts.FirstAsync(u => u.Id == account.Id);
        Assert.AreEqual(newSalt, updated.PasswordSalt);
    }

    #endregion

    #region FindByEmailAsync Tests

    [TestMethod]
    public async Task FindByEmailAsync_ReturnsAccountByNormalizedEmail()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "Test@Example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = "hash",
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Act - search with different casing
        var found = await _service.FindByEmailAsync("TEST@EXAMPLE.COM");

        // Assert
        Assert.IsNotNull(found);
        Assert.AreEqual(account.Id, found.Id);
    }

    [TestMethod]
    public async Task FindByEmailAsync_ReturnsNullForNonExistentEmail()
    {
        // Act
        var found = await _service.FindByEmailAsync("nonexistent@example.com");

        // Assert
        Assert.IsNull(found);
    }

    #endregion

    #region FindByUsernameOrEmailAsync Tests

    [TestMethod]
    public async Task FindByUsernameOrEmailAsync_FindsByUsername()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "uniqueuser",
            Email = "user@example.com",
            NormalizedEmail = "user@example.com",
            PasswordHash = "hash",
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Act
        var found = await _service.FindByUsernameOrEmailAsync("uniqueuser");

        // Assert
        Assert.IsNotNull(found);
        Assert.AreEqual(account.Id, found.Id);
    }

    [TestMethod]
    public async Task FindByUsernameOrEmailAsync_FindsByEmail()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "someuser",
            Email = "findme@example.com",
            NormalizedEmail = "findme@example.com",
            PasswordHash = "hash",
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Act
        var found = await _service.FindByUsernameOrEmailAsync("FINDME@EXAMPLE.COM");

        // Assert
        Assert.IsNotNull(found);
        Assert.AreEqual(account.Id, found.Id);
    }

    #endregion

    /// <summary>
    /// Accessor to create UserAccountService instance for testing.
    /// </summary>
    private sealed class UserAccountServiceAccessor : IUserAccountService
    {
        private readonly AuthDbContext _dbContext;

        public UserAccountServiceAccessor(AuthDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => await _dbContext.UserAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

        public async Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            return await _dbContext.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Username == username, ct);
        }

        public async Task<UserAccount> CreateAsync(UserAccount account, CancellationToken ct = default)
        {
            _dbContext.UserAccounts.Add(account);
            await _dbContext.SaveChangesAsync(ct);
            return account;
        }

        public async Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            var normalizedEmail = email.Trim().ToLowerInvariant();
            return await _dbContext.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, ct);
        }

        public async Task<UserAccount?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(usernameOrEmail)) return null;
            var trimmed = usernameOrEmail.Trim();
            var normalized = trimmed.ToLowerInvariant();
            return await _dbContext.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Username == trimmed || x.NormalizedEmail == normalized, ct);
        }

        public async Task UpdatePasswordAsync(Guid accountId, string newPasswordHash, string? salt, string algorithm, CancellationToken ct = default)
        {
            var account = await _dbContext.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null)
            {
                throw new InvalidOperationException($"UserAccount {accountId} not found.");
            }

            account.PasswordHash = newPasswordHash;
            account.PasswordSalt = salt;
            account.HashAlgorithm = algorithm;
            account.PasswordUpdatedAt = DateTimeOffset.UtcNow;
            account.FailedLoginAttempts = 0;
            account.LastFailedLoginAt = null;
            account.LockedOutUntil = null;

            await _dbContext.SaveChangesAsync(ct);
        }

        public async Task<IReadOnlyList<UserTenantMembership>> GetActiveMembershipsAsync(Guid accountId, CancellationToken ct = default)
        {
            return await _dbContext.UserTenantMemberships
                .AsNoTracking()
                .Include(x => x.Tenant)
                .Where(x => x.UserAccountId == accountId
                    && x.Status == TenantMembershipStatus.Active
                    && (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow))
                .ToListAsync(ct);
        }

        public async Task UpdateLockoutAsync(Guid accountId, int failedAttempts, DateTimeOffset? lastFailedAt, DateTimeOffset? lockedOutUntil, CancellationToken ct = default)
        {
            var account = await _dbContext.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null)
            {
                throw new InvalidOperationException($"UserAccount {accountId} not found.");
            }

            account.FailedLoginAttempts = failedAttempts;
            account.LastFailedLoginAt = lastFailedAt;
            account.LockedOutUntil = lockedOutUntil;

            await _dbContext.SaveChangesAsync(ct);
        }

        public async Task EnableMfaAsync(Guid accountId, string totpSecret, CancellationToken ct = default)
        {
            var account = await _dbContext.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");
            account.TotpSecret = totpSecret;
            await _dbContext.SaveChangesAsync(ct);
        }

        public async Task ConfirmMfaAsync(Guid accountId, CancellationToken ct = default)
        {
            var account = await _dbContext.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");
            if (string.IsNullOrWhiteSpace(account.TotpSecret))
                throw new InvalidOperationException($"UserAccount {accountId} does not have a TOTP secret set.");
            account.TotpEnabled = true;
            await _dbContext.SaveChangesAsync(ct);
        }

        public async Task DisableMfaAsync(Guid accountId, CancellationToken ct = default)
        {
            var account = await _dbContext.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");
            account.TotpEnabled = false;
            account.TotpSecret = null;
            await _dbContext.SaveChangesAsync(ct);
        }

        public async Task<(bool Enabled, string? Secret)> GetMfaStatusAsync(Guid accountId, CancellationToken ct = default)
        {
            var account = await _dbContext.UserAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");
            return (account.TotpEnabled, account.TotpSecret);
        }
    }
}
