using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Migration;

/// <summary>
/// Tests for password migration from per-tenant User to global UserAccount.
/// User Story 6: Migration of Existing Users
/// </summary>
[TestClass]
public sealed class PasswordMigrationServiceTests
{
    private AuthDbContext _db = null!;
    private IPasswordMigrationService _migrationService = null!;
    private IPasswordHasher _passwordHasher = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new AuthDbContext(options);
        _passwordHasher = new TestPasswordHasher();
        _migrationService = new PasswordMigrationService(_db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    [TestMethod]
    public async Task MigratePassword_UsesMostRecentPassword()
    {
        // Arrange: Create tenant and users with different passwords
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

        // Create users with same email but different passwords and creation times
        var olderUser = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant1.Id,
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = _passwordHasher.Hash("OldPassword123!"),
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30) // Older
        };
        var newerUser = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant2.Id,
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = _passwordHasher.Hash("NewPassword456!"),
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow // Newer
        };
        _db.Users.AddRange(olderUser, newerUser);

        // Create UserAccount without password
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            NormalizedEmail = "test@example.com",
            PasswordHash = string.Empty, // No password yet
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);

        // Create memberships linking users to account
        _db.UserTenantMemberships.AddRange(
            new UserTenantMembership
            {
                Id = Guid.NewGuid(),
                UserAccountId = account.Id,
                TenantId = tenant1.Id,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new UserTenantMembership
            {
                Id = Guid.NewGuid(),
                UserAccountId = account.Id,
                TenantId = tenant2.Id,
                CreatedAt = DateTimeOffset.UtcNow
            }
        );
        await _db.SaveChangesAsync();

        // Act: Migrate credentials
        var result = await _migrationService.MigrateUserCredentialsAsync(account.Id);

        // Assert: Most recent password (newer user's) is used
        Assert.IsTrue(result.Success);
        var updatedAccount = await _db.UserAccounts.FindAsync(account.Id);
        Assert.IsNotNull(updatedAccount);
        Assert.AreEqual(_passwordHasher.Hash("NewPassword456!"), updatedAccount.PasswordHash);
    }

    [TestMethod]
    public async Task MigratePassword_PreservesExistingPassword()
    {
        // Arrange: UserAccount already has a password
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Test Tenant",
            Slug = "test",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Tenants.Add(tenant);

        var existingPasswordHash = _passwordHasher.Hash("ExistingGlobalPassword!");
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "existinguser",
            Email = "existing@example.com",
            NormalizedEmail = "existing@example.com",
            PasswordHash = existingPasswordHash, // Already has password
            HashAlgorithm = "argon2id",
            PasswordUpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Username = "existinguser",
            Email = "existing@example.com",
            NormalizedEmail = "existing@example.com",
            PasswordHash = _passwordHasher.Hash("TenantPassword!"),
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Users.Add(user);

        _db.UserTenantMemberships.Add(new UserTenantMembership
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            TenantId = tenant.Id,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        // Act: Migrate - should skip since password already exists
        var result = await _migrationService.MigrateUserCredentialsAsync(account.Id);

        // Assert: Existing password preserved
        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Skipped); // Skipped because already has password
        var updatedAccount = await _db.UserAccounts.FindAsync(account.Id);
        Assert.IsNotNull(updatedAccount);
        Assert.AreEqual(existingPasswordHash, updatedAccount.PasswordHash);
    }

    [TestMethod]
    public async Task MigratePassword_MigratesMfaSettings()
    {
        // Arrange: User has MFA enabled in per-tenant record
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "MFA Tenant",
            Slug = "mfa",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Tenants.Add(tenant);

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "mfauser",
            Email = "mfa@example.com",
            NormalizedEmail = "mfa@example.com",
            PasswordHash = string.Empty,
            HashAlgorithm = "argon2id",
            TotpEnabled = false, // MFA not migrated yet
            TotpSecret = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Username = "mfauser",
            Email = "mfa@example.com",
            NormalizedEmail = "mfa@example.com",
            PasswordHash = _passwordHasher.Hash("MfaPassword!"),
            HashAlgorithm = "argon2id",
            TotpEnabled = true,
            TotpSecret = "JBSWY3DPEHPK3PXP",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Users.Add(user);

        _db.UserTenantMemberships.Add(new UserTenantMembership
        {
            Id = Guid.NewGuid(),
            UserAccountId = account.Id,
            TenantId = tenant.Id,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        // Act: Migrate credentials including MFA
        var result = await _migrationService.MigrateUserCredentialsAsync(account.Id);

        // Assert: MFA settings migrated
        Assert.IsTrue(result.Success);
        var updatedAccount = await _db.UserAccounts.FindAsync(account.Id);
        Assert.IsNotNull(updatedAccount);
        Assert.IsTrue(updatedAccount.TotpEnabled);
        Assert.AreEqual("JBSWY3DPEHPK3PXP", updatedAccount.TotpSecret);
    }

    [TestMethod]
    public async Task MigratePassword_HandlesNoLinkedUsers()
    {
        // Arrange: UserAccount with no linked per-tenant Users
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "orphanuser",
            Email = "orphan@example.com",
            NormalizedEmail = "orphan@example.com",
            PasswordHash = string.Empty,
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Act: Migrate - no users to migrate from
        var result = await _migrationService.MigrateUserCredentialsAsync(account.Id);

        // Assert: Marked as skipped (no source)
        Assert.IsTrue(result.Skipped);
        Assert.AreEqual("No linked users found to migrate from", result.Message);
    }

    [TestMethod]
    public async Task GetMigrationStatus_ReturnsCorrectCounts()
    {
        // Arrange: Create mix of migrated and unmigrated accounts
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Status Tenant",
            Slug = "status",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Tenants.Add(tenant);

        // Migrated account (has password)
        var migratedAccount = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "migrated",
            Email = "migrated@example.com",
            NormalizedEmail = "migrated@example.com",
            PasswordHash = _passwordHasher.Hash("Password!"),
            HashAlgorithm = "argon2id",
            PasswordUpdatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Unmigrated accounts (no password)
        var unmigrated1 = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "unmigrated1",
            Email = "unmigrated1@example.com",
            NormalizedEmail = "unmigrated1@example.com",
            PasswordHash = string.Empty,
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var unmigrated2 = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "unmigrated2",
            Email = "unmigrated2@example.com",
            NormalizedEmail = "unmigrated2@example.com",
            PasswordHash = string.Empty,
            HashAlgorithm = "argon2id",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.UserAccounts.AddRange(migratedAccount, unmigrated1, unmigrated2);
        await _db.SaveChangesAsync();

        // Act: Get migration status
        var status = await _migrationService.GetMigrationStatusAsync();

        // Assert
        Assert.AreEqual(3, status.TotalAccounts);
        Assert.AreEqual(1, status.MigratedAccounts);
        Assert.AreEqual(2, status.PendingAccounts);
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
