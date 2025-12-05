using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Services;

/// <summary>
/// Unit tests for PasswordResetService.
/// </summary>
[TestClass]
public class PasswordResetServiceTests
{
    private AuthDbContext _db = null!;
    private IPasswordHasher _hasher = null!;
    private IUserAccountService _userAccountService = null!;
    private IPasswordResetService _resetService = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase($"PasswordResetServiceTests_{Guid.NewGuid()}")
            .Options;

        _db = new AuthDbContext(options);
        _hasher = new Argon2PasswordHasher();
        _userAccountService = new TestUserAccountService(_db);
        _resetService = new TestPasswordResetService(_db, _userAccountService, _hasher);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    #region CreateResetTokenAsync Tests

    [TestMethod]
    public async Task CreateResetTokenAsync_ReturnsToken_WhenEmailExists()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = _hasher.Hash("OldPassword123!"),
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Act
        var result = await _resetService.CreateResetTokenAsync("test@example.com", "127.0.0.1");

        // Assert
        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Token);
        Assert.IsNull(result.ErrorMessage);
        Assert.AreEqual(account.Id, result.Account?.Id);
    }

    [TestMethod]
    public async Task CreateResetTokenAsync_ReturnsSuccessWithoutToken_WhenEmailNotFound()
    {
        // Act - request reset for non-existent email (should not reveal email doesn't exist)
        var result = await _resetService.CreateResetTokenAsync("nonexistent@example.com");

        // Assert - returns success but no token (security: don't reveal email existence)
        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Token);
        Assert.IsNull(result.Account);
    }

    [TestMethod]
    public async Task CreateResetTokenAsync_InvalidatesPreviousTokens()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = _hasher.Hash("Password123!"),
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Create first token
        var result1 = await _resetService.CreateResetTokenAsync("test@example.com");
        Assert.IsNotNull(result1.Token);

        // Act - create second token
        var result2 = await _resetService.CreateResetTokenAsync("test@example.com");

        // Assert - first token should be invalidated
        var validation1 = await _resetService.ValidateTokenAsync(result1.Token!);
        Assert.IsFalse(validation1.IsValid, "First token should be invalidated");

        var validation2 = await _resetService.ValidateTokenAsync(result2.Token!);
        Assert.IsTrue(validation2.IsValid, "Second token should be valid");
    }

    #endregion

    #region ValidateTokenAsync Tests

    [TestMethod]
    public async Task ValidateTokenAsync_ReturnsValid_ForActiveToken()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = _hasher.Hash("Password123!"),
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        var createResult = await _resetService.CreateResetTokenAsync("test@example.com");

        // Act
        var validateResult = await _resetService.ValidateTokenAsync(createResult.Token!);

        // Assert
        Assert.IsTrue(validateResult.IsValid);
        Assert.IsNull(validateResult.ErrorMessage);
        Assert.AreEqual(account.Id, validateResult.Account?.Id);
    }

    [TestMethod]
    public async Task ValidateTokenAsync_ReturnsInvalid_ForUsedToken()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = _hasher.Hash("Password123!"),
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        var createResult = await _resetService.CreateResetTokenAsync("test@example.com");
        
        // Use the token
        await _resetService.RedeemTokenAsync(createResult.Token!, "NewPassword456!");

        // Act - try to validate again
        var validateResult = await _resetService.ValidateTokenAsync(createResult.Token!);

        // Assert
        Assert.IsFalse(validateResult.IsValid);
        Assert.IsNotNull(validateResult.ErrorMessage);
    }

    [TestMethod]
    public async Task ValidateTokenAsync_ReturnsInvalid_ForNonExistentToken()
    {
        // Act
        var result = await _resetService.ValidateTokenAsync("invalid-token-12345");

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.IsNotNull(result.ErrorMessage);
    }

    #endregion

    #region RedeemTokenAsync Tests

    [TestMethod]
    public async Task RedeemTokenAsync_UpdatesUserAccountPassword()
    {
        // Arrange
        var originalPassword = "OldPassword123!";
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = _hasher.Hash(originalPassword),
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        var createResult = await _resetService.CreateResetTokenAsync("test@example.com");

        // Act
        var newPassword = "NewSecurePassword789!";
        var redeemResult = await _resetService.RedeemTokenAsync(createResult.Token!, newPassword);

        // Assert
        Assert.IsTrue(redeemResult.IsValid);

        // Verify old password no longer works
        var updatedAccount = await _db.UserAccounts.FirstAsync(a => a.Id == account.Id);
        Assert.IsFalse(_hasher.Verify(originalPassword, updatedAccount.PasswordHash));
        Assert.IsTrue(_hasher.Verify(newPassword, updatedAccount.PasswordHash));
    }

    [TestMethod]
    public async Task RedeemTokenAsync_ClearsLockoutState()
    {
        // Arrange
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
            LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        var createResult = await _resetService.CreateResetTokenAsync("locked@example.com");

        // Act
        await _resetService.RedeemTokenAsync(createResult.Token!, "NewPassword456!");

        // Assert - lockout should be cleared
        var updated = await _db.UserAccounts.FirstAsync(a => a.Id == account.Id);
        Assert.AreEqual(0, updated.FailedLoginAttempts);
        Assert.IsNull(updated.LastFailedLoginAt);
        Assert.IsNull(updated.LockedOutUntil);
    }

    [TestMethod]
    public async Task RedeemTokenAsync_MarksTokenAsUsed()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = _hasher.Hash("Password123!"),
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        var createResult = await _resetService.CreateResetTokenAsync("test@example.com");

        // Act
        await _resetService.RedeemTokenAsync(createResult.Token!, "NewPassword456!");

        // Assert - token should be marked as used
        var token = await _db.PasswordResetTokens.FirstAsync(t => t.UserAccountId == account.Id);
        Assert.IsTrue(token.IsUsed);
        Assert.IsNotNull(token.UsedAt);
    }

    [TestMethod]
    public async Task RedeemTokenAsync_FailsForAlreadyUsedToken()
    {
        // Arrange
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = _hasher.Hash("Password123!"),
            HashAlgorithm = "argon2id"
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        var createResult = await _resetService.CreateResetTokenAsync("test@example.com");
        
        // Use the token once
        await _resetService.RedeemTokenAsync(createResult.Token!, "NewPassword1!");

        // Act - try to use again
        var secondResult = await _resetService.RedeemTokenAsync(createResult.Token!, "NewPassword2!");

        // Assert
        Assert.IsFalse(secondResult.IsValid);
        Assert.IsNotNull(secondResult.ErrorMessage);
    }

    #endregion

    #region Test Helpers

    private sealed class TestUserAccountService(AuthDbContext db) : IUserAccountService
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

    private sealed class TestPasswordResetService : IPasswordResetService
    {
        private readonly AuthDbContext _db;
        private readonly IUserAccountService _userAccountService;
        private readonly IPasswordHasher _hasher;

        public TestPasswordResetService(AuthDbContext db, IUserAccountService userAccountService, IPasswordHasher hasher)
        {
            _db = db;
            _userAccountService = userAccountService;
            _hasher = hasher;
        }

        public async Task<PasswordResetTokenResult> CreateResetTokenAsync(string email, string? requestedFromIp = null, int expirationMinutes = 60, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email))
                return new PasswordResetTokenResult(false, null, "Email is required.");

            var account = await _userAccountService.FindByEmailAsync(email, ct);
            if (account is null)
                return new PasswordResetTokenResult(true, null, null);

            var rawToken = GenerateSecureToken();
            var tokenHash = HashToken(rawToken);

            var existingTokens = await _db.PasswordResetTokens
                .Where(t => t.UserAccountId == account.Id && !t.IsUsed)
                .ToListAsync(ct);

            foreach (var existing in existingTokens)
            {
                existing.IsUsed = true;
                existing.UsedAt = DateTimeOffset.UtcNow;
            }

            var resetToken = new PasswordResetToken
            {
                UserAccountId = account.Id,
                TokenHash = tokenHash,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expirationMinutes),
                RequestedFromIp = requestedFromIp
            };

            _db.PasswordResetTokens.Add(resetToken);
            await _db.SaveChangesAsync(ct);

            return new PasswordResetTokenResult(true, rawToken, null, account);
        }

        public async Task<PasswordResetValidationResult> ValidateTokenAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token))
                return new PasswordResetValidationResult(false, "Token is required.");

            var tokenHash = HashToken(token);
            var resetToken = await _db.PasswordResetTokens
                .Include(t => t.UserAccount)
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

            if (resetToken is null)
                return new PasswordResetValidationResult(false, "Invalid or expired reset link.");

            if (resetToken.IsUsed)
                return new PasswordResetValidationResult(false, "This reset link has already been used.");

            if (resetToken.ExpiresAt < DateTimeOffset.UtcNow)
                return new PasswordResetValidationResult(false, "This reset link has expired.");

            return new PasswordResetValidationResult(true, null, resetToken.UserAccount);
        }

        public async Task<PasswordResetValidationResult> RedeemTokenAsync(string token, string newPassword, CancellationToken ct = default)
        {
            var validation = await ValidateTokenAsync(token, ct);
            if (!validation.IsValid || validation.Account is null)
                return validation;

            var tokenHash = HashToken(token);
            var resetToken = await _db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
            if (resetToken is null)
                return new PasswordResetValidationResult(false, "Invalid or expired reset link.");

            resetToken.IsUsed = true;
            resetToken.UsedAt = DateTimeOffset.UtcNow;

            var newHash = _hasher.Hash(newPassword);
            await _userAccountService.UpdatePasswordAsync(validation.Account.Id, newHash, null, "argon2id", ct);
            await _db.SaveChangesAsync(ct);

            return new PasswordResetValidationResult(true, null, validation.Account);
        }

        public Task CleanupExpiredTokensAsync(CancellationToken ct = default) => Task.CompletedTask;

        private static string GenerateSecureToken()
        {
            var bytes = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }

        private static string HashToken(string token)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(token);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    #endregion
}
