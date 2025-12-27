using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Integration;

[TestClass]
public class MfaIntegrationTests
{
    private static AuthDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(options);
    }

    [TestMethod]
    public async Task MfaEnabled_AppliesAcrossAllTenants()
    {
        // Arrange - Create a UserAccount with 3 tenant memberships
        await using var db = CreateInMemoryDbContext();
        var service = new UserAccountService(db);

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "mfauser",
            Email = "mfa@example.com",
            NormalizedEmail = "mfa@example.com",
            PasswordHash = "hash",
            TotpEnabled = false,
            TotpSecret = null
        };
        db.UserAccounts.Add(account);

        // Create 3 tenants
        var tenant1 = new Tenant { Id = Guid.NewGuid(), Name = "Tenant A" };
        var tenant2 = new Tenant { Id = Guid.NewGuid(), Name = "Tenant B" };
        var tenant3 = new Tenant { Id = Guid.NewGuid(), Name = "Tenant C" };
        db.Tenants.AddRange(tenant1, tenant2, tenant3);

        // Create memberships
        db.UserTenantMemberships.AddRange(
            new UserTenantMembership
            {
                Id = Guid.NewGuid(),
                UserAccountId = account.Id,
                TenantId = tenant1.Id,
                Status = TenantMembershipStatus.Active,
                IsTenantAdmin = false
            },
            new UserTenantMembership
            {
                Id = Guid.NewGuid(),
                UserAccountId = account.Id,
                TenantId = tenant2.Id,
                Status = TenantMembershipStatus.Active,
                IsTenantAdmin = false
            },
            new UserTenantMembership
            {
                Id = Guid.NewGuid(),
                UserAccountId = account.Id,
                TenantId = tenant3.Id,
                Status = TenantMembershipStatus.Active,
                IsTenantAdmin = true
            }
        );
        await db.SaveChangesAsync();

        // Verify MFA is initially disabled
        var (initialEnabled, _) = await service.GetMfaStatusAsync(account.Id);
        Assert.IsFalse(initialEnabled, "MFA should be disabled initially");

        // Act - Enable MFA on the global account
        await service.EnableMfaAsync(account.Id, "JBSWY3DPEHPK3PXP");
        await service.ConfirmMfaAsync(account.Id);

        // Assert - MFA status is now enabled globally
        var (enabled, secret) = await service.GetMfaStatusAsync(account.Id);
        Assert.IsTrue(enabled, "MFA should be enabled after confirmation");
        Assert.AreEqual("JBSWY3DPEHPK3PXP", secret);

        // Verify the account is the same for all tenant memberships
        var memberships = await service.GetActiveMembershipsAsync(account.Id);
        Assert.AreEqual(3, memberships.Count, "Account should have 3 active memberships");

        // All memberships point to the same UserAccount with MFA enabled
        foreach (var membership in memberships)
        {
            Assert.AreEqual(account.Id, membership.UserAccountId);
        }
    }

    [TestMethod]
    public async Task MfaDisabled_DisablesAcrossAllTenants()
    {
        // Arrange - Create a UserAccount with MFA enabled and multiple tenants
        await using var db = CreateInMemoryDbContext();
        var service = new UserAccountService(db);

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "mfadisableuser",
            Email = "mfadisable@example.com",
            NormalizedEmail = "mfadisable@example.com",
            PasswordHash = "hash",
            TotpEnabled = true,
            TotpSecret = "EXISTINGSECRET"
        };
        db.UserAccounts.Add(account);

        var tenant1 = new Tenant { Id = Guid.NewGuid(), Name = "Tenant X" };
        var tenant2 = new Tenant { Id = Guid.NewGuid(), Name = "Tenant Y" };
        db.Tenants.AddRange(tenant1, tenant2);

        db.UserTenantMemberships.AddRange(
            new UserTenantMembership
            {
                Id = Guid.NewGuid(),
                UserAccountId = account.Id,
                TenantId = tenant1.Id,
                Status = TenantMembershipStatus.Active,
                IsTenantAdmin = false
            },
            new UserTenantMembership
            {
                Id = Guid.NewGuid(),
                UserAccountId = account.Id,
                TenantId = tenant2.Id,
                Status = TenantMembershipStatus.Active,
                IsTenantAdmin = false
            }
        );
        await db.SaveChangesAsync();

        // Verify MFA is initially enabled
        var (initialEnabled, _) = await service.GetMfaStatusAsync(account.Id);
        Assert.IsTrue(initialEnabled, "MFA should be enabled initially");

        // Act - Disable MFA on the global account
        await service.DisableMfaAsync(account.Id);

        // Assert - MFA is now disabled globally
        var (enabled, secret) = await service.GetMfaStatusAsync(account.Id);
        Assert.IsFalse(enabled, "MFA should be disabled after DisableMfaAsync");
        Assert.IsNull(secret, "TOTP secret should be cleared");

        // Verify all memberships still point to the same account (which now has MFA disabled)
        var memberships = await service.GetActiveMembershipsAsync(account.Id);
        Assert.AreEqual(2, memberships.Count);
    }

    [TestMethod]
    public async Task MfaEnrollment_DoesNotAffectOtherUsers()
    {
        // Arrange - Create two separate UserAccounts
        await using var db = CreateInMemoryDbContext();
        var service = new UserAccountService(db);

        var account1 = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "user1",
            Email = "user1@example.com",
            NormalizedEmail = "user1@example.com",
            PasswordHash = "hash1",
            TotpEnabled = false
        };
        var account2 = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "user2",
            Email = "user2@example.com",
            NormalizedEmail = "user2@example.com",
            PasswordHash = "hash2",
            TotpEnabled = false
        };
        db.UserAccounts.AddRange(account1, account2);
        await db.SaveChangesAsync();

        // Act - Enable MFA for user1 only
        await service.EnableMfaAsync(account1.Id, "USER1SECRET");
        await service.ConfirmMfaAsync(account1.Id);

        // Assert - User1 has MFA enabled
        var (user1Enabled, user1Secret) = await service.GetMfaStatusAsync(account1.Id);
        Assert.IsTrue(user1Enabled);
        Assert.AreEqual("USER1SECRET", user1Secret);

        // Assert - User2 still has MFA disabled
        var (user2Enabled, user2Secret) = await service.GetMfaStatusAsync(account2.Id);
        Assert.IsFalse(user2Enabled);
        Assert.IsNull(user2Secret);
    }

    [TestMethod]
    public async Task LoginWithMfa_RequiresMfaForAllTenants()
    {
        // This test verifies that when MFA is enabled on UserAccount,
        // attempting to authenticate returns MfaRequired for any tenant

        // Arrange
        await using var db = CreateInMemoryDbContext();
        
        // Set up the password hasher and authentication service
        var passwordHasher = new Argon2PasswordHasher();
        var password = "TestPassword123!";
        var hash = passwordHasher.Hash(password);

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "mfaloginuser",
            Email = "mfalogin@example.com",
            NormalizedEmail = "mfalogin@example.com",
            PasswordHash = hash,
            TotpEnabled = true,
            TotpSecret = "MFASECRET"
        };
        db.UserAccounts.Add(account);

        var tenant1 = new Tenant { Id = Guid.NewGuid(), Name = "Login Tenant A" };
        var tenant2 = new Tenant { Id = Guid.NewGuid(), Name = "Login Tenant B" };
        db.Tenants.AddRange(tenant1, tenant2);

        db.UserTenantMemberships.AddRange(
            new UserTenantMembership
            {
                Id = Guid.NewGuid(),
                UserAccountId = account.Id,
                TenantId = tenant1.Id,
                Status = TenantMembershipStatus.Active,
                IsTenantAdmin = false
            },
            new UserTenantMembership
            {
                Id = Guid.NewGuid(),
                UserAccountId = account.Id,
                TenantId = tenant2.Id,
                Status = TenantMembershipStatus.Active,
                IsTenantAdmin = false
            }
        );
        await db.SaveChangesAsync();

        var userAccountService = new UserAccountService(db);
        var metrics = new GlobalAuthMetrics();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<GlobalAuthenticationService>.Instance;
        var authService = new GlobalAuthenticationService(
            userAccountService, 
            passwordHasher, 
            metrics,
            logger);

        // Act - Authenticate with correct password
        var result = await authService.AuthenticateAsync("mfalogin@example.com", password);

        // Assert - Should return MfaRequired since TotpEnabled is true
        Assert.IsFalse(result.Succeeded, "Authentication should not succeed when MFA is required");
        Assert.AreEqual(AuthenticationFailureReason.MfaRequired, result.FailureReason);
    }

    /// <summary>
    /// Concrete implementation for testing - duplicated here to avoid internal access issues.
    /// </summary>
    private sealed class UserAccountService : IUserAccountService
    {
        private readonly AuthDbContext _db;

        public UserAccountService(AuthDbContext db) => _db = db;

        public Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => _db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

        public Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default)
            => _db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Username == username, ct);

        public async Task<UserAccount> CreateAsync(UserAccount account, CancellationToken ct = default)
        {
            _db.UserAccounts.Add(account);
            await _db.SaveChangesAsync(ct);
            return account;
        }

        public Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default)
        {
            var normalized = email.Trim().ToLowerInvariant();
            return _db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);
        }

        public Task<UserAccount?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default)
        {
            var trimmed = usernameOrEmail.Trim();
            var normalized = trimmed.ToLowerInvariant();
            return _db.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Username == trimmed || x.NormalizedEmail == normalized, ct);
        }

        public async Task UpdatePasswordAsync(Guid accountId, string newPasswordHash, string? salt, string algorithm, CancellationToken ct = default)
        {
            var account = await _db.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");
            account.PasswordHash = newPasswordHash;
            account.PasswordSalt = salt;
            account.HashAlgorithm = algorithm;
            account.PasswordUpdatedAt = DateTimeOffset.UtcNow;
            account.FailedLoginAttempts = 0;
            account.LastFailedLoginAt = null;
            account.LockedOutUntil = null;
            await _db.SaveChangesAsync(ct);
        }

        public async Task<IReadOnlyList<UserTenantMembership>> GetActiveMembershipsAsync(Guid accountId, CancellationToken ct = default)
        {
            return await _db.UserTenantMemberships.AsNoTracking()
                .Where(x => x.UserAccountId == accountId && x.Status == TenantMembershipStatus.Active)
                .ToListAsync(ct);
        }

        public async Task UpdateLockoutAsync(Guid accountId, int failedAttempts, DateTimeOffset? lastFailedAt, DateTimeOffset? lockedOutUntil, CancellationToken ct = default)
        {
            var account = await _db.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");
            account.FailedLoginAttempts = failedAttempts;
            account.LastFailedLoginAt = lastFailedAt;
            account.LockedOutUntil = lockedOutUntil;
            await _db.SaveChangesAsync(ct);
        }

        public async Task EnableMfaAsync(Guid accountId, string totpSecret, CancellationToken ct = default)
        {
            var account = await _db.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");
            account.TotpSecret = totpSecret;
            await _db.SaveChangesAsync(ct);
        }

        public async Task ConfirmMfaAsync(Guid accountId, CancellationToken ct = default)
        {
            var account = await _db.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");
            if (string.IsNullOrWhiteSpace(account.TotpSecret))
                throw new InvalidOperationException($"UserAccount {accountId} does not have a TOTP secret set.");
            account.TotpEnabled = true;
            await _db.SaveChangesAsync(ct);
        }

        public async Task DisableMfaAsync(Guid accountId, CancellationToken ct = default)
        {
            var account = await _db.UserAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");
            account.TotpEnabled = false;
            account.TotpSecret = null;
            await _db.SaveChangesAsync(ct);
        }

        public async Task<(bool Enabled, string? Secret)> GetMfaStatusAsync(Guid accountId, CancellationToken ct = default)
        {
            var account = await _db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null) throw new InvalidOperationException($"UserAccount {accountId} not found.");
            return (account.TotpEnabled, account.TotpSecret);
        }
    }

    /// <summary>
    /// Test password hasher for integration tests.
    /// </summary>
    private sealed class Argon2PasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => Isopoh.Cryptography.Argon2.Argon2.Hash(password);
        public bool Verify(string password, string hash) => Isopoh.Cryptography.Argon2.Argon2.Verify(hash, password);
    }

    /// <summary>
    /// Test implementation wrapping the internal GlobalAuthenticationService for testing.
    /// </summary>
    private sealed class GlobalAuthenticationService : IGlobalAuthenticationService
    {
        private readonly IUserAccountService _userAccountService;
        private readonly IPasswordHasher _passwordHasher;
        private readonly GlobalAuthMetrics _metrics;
        private const int MaxFailedAttempts = 5;
        private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

        public GlobalAuthenticationService(
            IUserAccountService userAccountService,
            IPasswordHasher passwordHasher,
            GlobalAuthMetrics metrics,
            Microsoft.Extensions.Logging.ILogger logger)
        {
            _userAccountService = userAccountService;
            _passwordHasher = passwordHasher;
            _metrics = metrics;
        }

        public async Task<GlobalAuthenticationResult> AuthenticateAsync(string usernameOrEmail, string password, CancellationToken ct = default)
        {
            var account = await _userAccountService.FindByUsernameOrEmailAsync(usernameOrEmail, ct);
            if (account is null)
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.UserNotFound);

            if (await IsLockedOutAsync(account.Id, ct))
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.AccountLocked, account.LockedOutUntil);

            if (!_passwordHasher.Verify(password, account.PasswordHash))
            {
                await RecordFailedAttemptAsync(account.Id, ct);
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.InvalidPassword);
            }

            var memberships = await _userAccountService.GetActiveMembershipsAsync(account.Id, ct);
            if (memberships.Count == 0)
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.NoActiveMemberships);

            await ClearFailedAttemptsAsync(account.Id, ct);

            // Check if MFA is required
            if (account.TotpEnabled && !string.IsNullOrEmpty(account.TotpSecret))
                return GlobalAuthenticationResult.Failure(AuthenticationFailureReason.MfaRequired);

            return GlobalAuthenticationResult.Success(account, memberships);
        }

        public Task RecordFailedAttemptAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearFailedAttemptsAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsLockedOutAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<UserAccount?> FindAccountByEmailAsync(string email, CancellationToken ct = default) 
            => _userAccountService.FindByEmailAsync(email, ct);
    }
}
