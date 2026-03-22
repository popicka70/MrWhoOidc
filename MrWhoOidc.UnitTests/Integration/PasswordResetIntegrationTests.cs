using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Integration;

/// <summary>
/// Integration tests for password reset functionality with global credentials.
/// Verifies that password reset restores access across all tenants.
/// </summary>
[TestClass]
public class PasswordResetIntegrationTests
{
    private AuthDbContext _db = null!;
    private IPasswordHasher _hasher = null!;
    private IUserAccountService _userAccountService = null!;
    private IPasswordResetService _resetService = null!;
    private IGlobalAuthenticationService _globalAuthService = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase($"PasswordResetIntegrationTests_{Guid.NewGuid()}")
            .Options;

        _db = new AuthDbContext(options);
        _hasher = new TestPasswordHasher();
        _userAccountService = new TestUserAccountService(_db);
        _resetService = new TestPasswordResetService(_db, _userAccountService, _hasher);
        _globalAuthService = new TestGlobalAuthService(_userAccountService, _hasher);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    [TestMethod]
    public async Task PasswordReset_RestoresAccessToAllTenants()
    {
        // Arrange: Create a user with access to multiple tenants
        var tenant1 = new Tenant { Id = Guid.NewGuid(), Name = "Tenant One", Slug = "tenant1" };
        var tenant2 = new Tenant { Id = Guid.NewGuid(), Name = "Tenant Two", Slug = "tenant2" };
        var tenant3 = new Tenant { Id = Guid.NewGuid(), Name = "Tenant Three", Slug = "tenant3" };

        _db.Tenants.AddRange(tenant1, tenant2, tenant3);

        var originalPassword = "ForgottenPassword123!";
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

        // User can login with original password
        var authBefore = await _globalAuthService.AuthenticateAsync(account.Email!, originalPassword);
        Assert.IsTrue(authBefore.Succeeded, "User should be able to login before reset");
        Assert.AreEqual(3, authBefore.Memberships.Count);

        // Act: Request and complete password reset
        var resetResult = await _resetService.CreateResetTokenAsync(account.Email!);
        Assert.IsNotNull(resetResult.Token, "Should receive reset token");

        var newPassword = "NewSecurePassword456!";
        var redeemResult = await _resetService.RedeemTokenAsync(resetResult.Token!, newPassword);
        Assert.IsTrue(redeemResult.IsValid, "Token redemption should succeed");

        // Assert: Old password no longer works
        var authOldPwd = await _globalAuthService.AuthenticateAsync(account.Email!, originalPassword);
        Assert.IsFalse(authOldPwd.Succeeded, "Old password should not work after reset");

        // Assert: New password works and grants access to all tenants
        var authNewPwd = await _globalAuthService.AuthenticateAsync(account.Email!, newPassword);
        Assert.IsTrue(authNewPwd.Succeeded, "New password should work after reset");
        Assert.AreEqual(3, authNewPwd.Memberships.Count, "Should still have access to all three tenants");
    }

    [TestMethod]
    public async Task PasswordReset_ClearsAccountLockout()
    {
        // Arrange: Create a locked-out user
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

        // Verify account is locked
        var authLocked = await _globalAuthService.AuthenticateAsync(account.Email!, "OldPassword123!");
        Assert.IsFalse(authLocked.Succeeded, "Account should be locked");
        Assert.AreEqual(AuthenticationFailureReason.AccountLocked, authLocked.FailureReason);

        // Act: Reset password
        var resetResult = await _resetService.CreateResetTokenAsync(account.Email!);
        var newPassword = "NewPassword789!";
        await _resetService.RedeemTokenAsync(resetResult.Token!, newPassword);

        // Assert: Account is unlocked and new password works immediately
        var authAfter = await _globalAuthService.AuthenticateAsync(account.Email!, newPassword);
        Assert.IsTrue(authAfter.Succeeded, "New password should work immediately after reset");
    }

    [TestMethod]
    public async Task PasswordReset_TokenCanOnlyBeUsedOnce()
    {
        // Arrange
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Test", Slug = "test" };
        _db.Tenants.Add(tenant);

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
        _db.UserTenantMemberships.Add(new UserTenantMembership
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            TenantId = tenant.Id,
            Status = TenantMembershipStatus.Active
        });
        await _db.SaveChangesAsync();

        // Act: Create and use a reset token
        var resetResult = await _resetService.CreateResetTokenAsync(account.Email!);
        var firstRedeem = await _resetService.RedeemTokenAsync(resetResult.Token!, "FirstNewPassword!");
        Assert.IsTrue(firstRedeem.IsValid, "First redemption should succeed");

        // Try to use the same token again
        var secondRedeem = await _resetService.RedeemTokenAsync(resetResult.Token!, "SecondNewPassword!");

        // Assert
        Assert.IsFalse(secondRedeem.IsValid, "Second redemption should fail");
        Assert.IsNotNull(secondRedeem.ErrorMessage);
    }

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

        public Task EnableMfaAsync(Guid accountId, string totpSecret, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task ConfirmMfaAsync(Guid accountId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task DisableMfaAsync(Guid accountId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<(bool Enabled, string? Secret)> GetMfaStatusAsync(Guid accountId, CancellationToken ct = default)
            => throw new NotImplementedException();
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

    private sealed class TestGlobalAuthService(IUserAccountService userAccountService, IPasswordHasher hasher) : IGlobalAuthenticationService
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
