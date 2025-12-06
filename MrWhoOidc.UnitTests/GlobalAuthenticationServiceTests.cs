using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class GlobalAuthenticationServiceTests
{
    private static readonly Guid DefaultTenantId = new("00000000-0000-0000-0000-000000000001");

    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static async Task<UserAccount> SeedUserAccountWithMembership(
        AuthDbContext db,
        string username = "alice",
        string email = "alice@example.com",
        string password = "secret123",
        bool totpEnabled = false)
    {
        var hasher = new DummyHasher();
        var account = new UserAccount
        {
            Username = username,
            Email = email,
            NormalizedEmail = email.ToLowerInvariant(),
            PasswordHash = hasher.Hash(password),
            TotpEnabled = totpEnabled,
            TotpSecret = totpEnabled ? "TESTSECRET" : null
        };
        db.UserAccounts.Add(account);

        // Create a tenant and membership
        var tenant = new Tenant
        {
            Id = DefaultTenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://localhost/default"
        };
        db.Tenants.Add(tenant);

        var membership = new UserTenantMembership
        {
            UserAccountId = account.Id,
            TenantId = tenant.Id,
            Status = TenantMembershipStatus.Active
        };
        db.UserTenantMemberships.Add(membership);

        await db.SaveChangesAsync();
        return account;
    }

    [TestMethod]
    public async Task AuthenticateAsync_ValidCredentials_ReturnsSuccess()
    {
        // Arrange
        using var db = CreateDb();
        var account = await SeedUserAccountWithMembership(db);
        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        var result = await svc.AuthenticateAsync("alice", "secret123");

        // Assert
        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Account);
        Assert.AreEqual(account.Id, result.Account.Id);
        Assert.AreEqual(1, result.Memberships.Count);
        Assert.IsNull(result.FailureReason);
    }

    [TestMethod]
    public async Task AuthenticateAsync_ValidCredentialsByEmail_ReturnsSuccess()
    {
        // Arrange
        using var db = CreateDb();
        var account = await SeedUserAccountWithMembership(db);
        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        var result = await svc.AuthenticateAsync("alice@example.com", "secret123");

        // Assert
        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Account);
        Assert.AreEqual(account.Id, result.Account.Id);
    }

    [TestMethod]
    public async Task AuthenticateAsync_InvalidPassword_ReturnsFailure()
    {
        // Arrange
        using var db = CreateDb();
        await SeedUserAccountWithMembership(db);
        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        var result = await svc.AuthenticateAsync("alice", "wrongpassword");

        // Assert
        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Account);
        Assert.AreEqual(AuthenticationFailureReason.InvalidPassword, result.FailureReason);
    }

    [TestMethod]
    public async Task AuthenticateAsync_UserNotFound_ReturnsUserNotFound()
    {
        // Arrange
        using var db = CreateDb();
        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        var result = await svc.AuthenticateAsync("nonexistent@example.com", "password");

        // Assert
        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Account);
        Assert.AreEqual(AuthenticationFailureReason.UserNotFound, result.FailureReason);
    }

    [TestMethod]
    public async Task AuthenticateAsync_NoActiveMemberships_ReturnsNoActiveMemberships()
    {
        // Arrange
        using var db = CreateDb();
        var hasher = new DummyHasher();
        
        // Create user without any memberships
        var account = new UserAccount
        {
            Username = "bob",
            Email = "bob@example.com",
            NormalizedEmail = "bob@example.com",
            PasswordHash = hasher.Hash("secret123")
        };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        var result = await svc.AuthenticateAsync("bob", "secret123");

        // Assert
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(AuthenticationFailureReason.NoActiveMemberships, result.FailureReason);
    }

    [TestMethod]
    public async Task AuthenticateAsync_AccountLocked_ReturnsAccountLocked()
    {
        // Arrange
        using var db = CreateDb();
        var account = await SeedUserAccountWithMembership(db);
        
        // Lock the account
        account.LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        account.FailedLoginAttempts = 5;
        await db.SaveChangesAsync();

        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        var result = await svc.AuthenticateAsync("alice", "secret123");

        // Assert
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(AuthenticationFailureReason.AccountLocked, result.FailureReason);
        Assert.IsNotNull(result.LockedUntil);
    }

    [TestMethod]
    public async Task AuthenticateAsync_MfaEnabled_ReturnsMfaRequired()
    {
        // Arrange
        using var db = CreateDb();
        await SeedUserAccountWithMembership(db, totpEnabled: true);
        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        var result = await svc.AuthenticateAsync("alice", "secret123");

        // Assert
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(AuthenticationFailureReason.MfaRequired, result.FailureReason);
    }

    [TestMethod]
    public async Task RecordFailedAttemptAsync_IncrementsCounter()
    {
        // Arrange
        using var db = CreateDb();
        var account = await SeedUserAccountWithMembership(db);
        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        await svc.RecordFailedAttemptAsync(account.Id);

        // Assert
        var updatedAccount = await db.UserAccounts.FirstAsync(x => x.Id == account.Id);
        Assert.AreEqual(1, updatedAccount.FailedLoginAttempts);
        Assert.IsNotNull(updatedAccount.LastFailedLoginAt);
    }

    [TestMethod]
    public async Task RecordFailedAttemptAsync_LocksAccountAfterMaxAttempts()
    {
        // Arrange
        using var db = CreateDb();
        var account = await SeedUserAccountWithMembership(db);
        account.FailedLoginAttempts = 4; // One more will trigger lockout
        await db.SaveChangesAsync();

        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        await svc.RecordFailedAttemptAsync(account.Id);

        // Assert
        var updatedAccount = await db.UserAccounts.FirstAsync(x => x.Id == account.Id);
        Assert.AreEqual(5, updatedAccount.FailedLoginAttempts);
        Assert.IsNotNull(updatedAccount.LockedOutUntil);
        Assert.IsTrue(updatedAccount.LockedOutUntil > DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public async Task ClearFailedAttemptsAsync_ResetsCounter()
    {
        // Arrange
        using var db = CreateDb();
        var account = await SeedUserAccountWithMembership(db);
        account.FailedLoginAttempts = 3;
        account.LastFailedLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        await svc.ClearFailedAttemptsAsync(account.Id);

        // Assert
        var updatedAccount = await db.UserAccounts.FirstAsync(x => x.Id == account.Id);
        Assert.AreEqual(0, updatedAccount.FailedLoginAttempts);
        Assert.IsNull(updatedAccount.LastFailedLoginAt);
        Assert.IsNull(updatedAccount.LockedOutUntil);
    }

    [TestMethod]
    public async Task IsLockedOutAsync_ReturnsTrueWhenLocked()
    {
        // Arrange
        using var db = CreateDb();
        var account = await SeedUserAccountWithMembership(db);
        account.LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        await db.SaveChangesAsync();

        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        var isLocked = await svc.IsLockedOutAsync(account.Id);

        // Assert
        Assert.IsTrue(isLocked);
    }

    [TestMethod]
    public async Task IsLockedOutAsync_ReturnsFalseWhenNotLocked()
    {
        // Arrange
        using var db = CreateDb();
        var account = await SeedUserAccountWithMembership(db);
        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        var isLocked = await svc.IsLockedOutAsync(account.Id);

        // Assert
        Assert.IsFalse(isLocked);
    }

    [TestMethod]
    public async Task IsLockedOutAsync_ReturnsFalseWhenLockoutExpired()
    {
        // Arrange
        using var db = CreateDb();
        var account = await SeedUserAccountWithMembership(db);
        account.LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(-1); // Expired
        await db.SaveChangesAsync();

        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        var isLocked = await svc.IsLockedOutAsync(account.Id);

        // Assert
        Assert.IsFalse(isLocked);
    }

    [TestMethod]
    public async Task AuthenticateAsync_SuccessfulLogin_ClearsFailedAttempts()
    {
        // Arrange
        using var db = CreateDb();
        var account = await SeedUserAccountWithMembership(db);
        account.FailedLoginAttempts = 3;
        account.LastFailedLoginAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();

        var userAccountService = new UserAccountService(db, NullLogger<UserAccountService>.Instance);
        var hasher = new DummyHasher();
        var metrics = new OidcMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;

        var svc = new GlobalAuthenticationService(userAccountService, hasher, metrics, logger);

        // Act
        var result = await svc.AuthenticateAsync("alice", "secret123");

        // Assert
        Assert.IsTrue(result.Succeeded);
        var updatedAccount = await db.UserAccounts.FirstAsync(x => x.Id == account.Id);
        Assert.AreEqual(0, updatedAccount.FailedLoginAttempts);
    }

    private sealed class DummyHasher : IPasswordHasher
    {
        public string Hash(string password) => password;
        public bool Verify(string password, string hash) => hash == password;
    }
}
