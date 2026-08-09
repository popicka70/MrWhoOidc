using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.MultiTenancy;

/// <summary>
/// Integration tests for global authentication across multiple tenants.
/// Verifies that a single set of credentials works across all tenant memberships.
/// </summary>
[TestClass]
public class GlobalAuthenticationIntegrationTests
{
    private AuthDbContext _db = null!;
    private IGlobalAuthenticationService _globalAuthService = null!;
    private IUserAccountService _userAccountService = null!;

    private Tenant _tenant1 = null!;
    private Tenant _tenant2 = null!;
    private Tenant _tenant3 = null!;
    private UserAccount _userAccount = null!;

    [TestInitialize]
    public async Task Setup()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AuthDbContext(opts);

        // Create three tenants
        _tenant1 = new Tenant
        {
            Slug = "tenant-alpha",
            Name = "Tenant Alpha",
            IssuerUri = "https://alpha.example.com",
            Status = TenantStatus.Active
        };
        _tenant2 = new Tenant
        {
            Slug = "tenant-beta",
            Name = "Tenant Beta",
            IssuerUri = "https://beta.example.com",
            Status = TenantStatus.Active
        };
        _tenant3 = new Tenant
        {
            Slug = "tenant-gamma",
            Name = "Tenant Gamma",
            IssuerUri = "https://gamma.example.com",
            Status = TenantStatus.Active
        };
        _db.Tenants.AddRange(_tenant1, _tenant2, _tenant3);

        // Create a user account with a global password
        var hasher = new DummyHasher();
        _userAccount = new UserAccount
        {
            Username = "globaluser",
            Email = "global@example.com",
            NormalizedEmail = "global@example.com",
            EmailVerified = true, // H6: unconfirmed emails are blocked unless the realm allows them
            PasswordHash = hasher.Hash("GlobalPassword123!")
        };
        _db.UserAccounts.Add(_userAccount);

        // Create memberships for tenant1 and tenant2 (NOT tenant3)
        _db.UserTenantMemberships.Add(new UserTenantMembership
        {
            UserAccountId = _userAccount.Id,
            TenantId = _tenant1.Id,
            Status = TenantMembershipStatus.Active
        });
        _db.UserTenantMemberships.Add(new UserTenantMembership
        {
            UserAccountId = _userAccount.Id,
            TenantId = _tenant2.Id,
            Status = TenantMembershipStatus.Active
        });

        await _db.SaveChangesAsync();

        // Create services
        _userAccountService = new UserAccountService(_db, NullLogger<UserAccountService>.Instance);
        var metrics = new GlobalAuthMetrics();
        var logger = NullLogger<GlobalAuthenticationService>.Instance;
        _globalAuthService = new GlobalAuthenticationService(_userAccountService, hasher, metrics, logger);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
    }

    [TestMethod]
    public async Task User_CanLoginToMultipleTenants_WithSameCredentials()
    {
        // Arrange - same credentials
        const string username = "globaluser";
        const string password = "GlobalPassword123!";

        // Act - authenticate (simulating login to tenant1)
        var result1 = await _globalAuthService.AuthenticateAsync(username, password);

        // Assert - first login succeeds
        Assert.IsTrue(result1.Succeeded, "First login should succeed");
        Assert.IsNotNull(result1.Account);
        Assert.AreEqual(_userAccount.Id, result1.Account.Id);

        // The result should include both tenant memberships
        Assert.AreEqual(2, result1.Memberships.Count);
        Assert.IsTrue(result1.Memberships.Any(m => m.TenantId == _tenant1.Id));
        Assert.IsTrue(result1.Memberships.Any(m => m.TenantId == _tenant2.Id));

        // Act - authenticate again (simulating login to tenant2)
        var result2 = await _globalAuthService.AuthenticateAsync(username, password);

        // Assert - second login also succeeds with same credentials
        Assert.IsTrue(result2.Succeeded, "Second login should succeed");
        Assert.AreEqual(_userAccount.Id, result2.Account!.Id);
        Assert.AreEqual(2, result2.Memberships.Count);
    }

    [TestMethod]
    public async Task User_CanLoginByEmail_AcrossTenants()
    {
        // Arrange
        const string email = "global@example.com";
        const string password = "GlobalPassword123!";

        // Act
        var result = await _globalAuthService.AuthenticateAsync(email, password);

        // Assert
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(_userAccount.Id, result.Account!.Id);
        Assert.AreEqual(2, result.Memberships.Count);
    }

    [TestMethod]
    public async Task GlobalAuth_ReturnsMemberships_ForAllActiveTenants()
    {
        // Arrange
        const string username = "globaluser";
        const string password = "GlobalPassword123!";

        // Act
        var result = await _globalAuthService.AuthenticateAsync(username, password);

        // Assert
        Assert.IsTrue(result.Succeeded);

        // Should have memberships for tenant1 and tenant2
        var tenantIds = result.Memberships.Select(m => m.TenantId).ToList();
        Assert.IsTrue(tenantIds.Contains(_tenant1.Id), "Should have membership for tenant1");
        Assert.IsTrue(tenantIds.Contains(_tenant2.Id), "Should have membership for tenant2");
        Assert.IsFalse(tenantIds.Contains(_tenant3.Id), "Should NOT have membership for tenant3");
    }

    [TestMethod]
    public async Task GlobalAuth_ExcludesSuspendedMemberships()
    {
        // Arrange - suspend the membership for tenant2
        var membership = await _db.UserTenantMemberships
            .FirstAsync(m => m.UserAccountId == _userAccount.Id && m.TenantId == _tenant2.Id);
        membership.Status = TenantMembershipStatus.Suspended;
        await _db.SaveChangesAsync();

        // Act
        var result = await _globalAuthService.AuthenticateAsync("globaluser", "GlobalPassword123!");

        // Assert - only tenant1 membership should be returned
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Memberships.Count);
        Assert.AreEqual(_tenant1.Id, result.Memberships.First().TenantId);
    }

    [TestMethod]
    public async Task GlobalAuth_ExcludesExpiredMemberships()
    {
        // Arrange - expire the membership for tenant2
        var membership = await _db.UserTenantMemberships
            .FirstAsync(m => m.UserAccountId == _userAccount.Id && m.TenantId == _tenant2.Id);
        membership.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1); // Expired yesterday
        await _db.SaveChangesAsync();

        // Act
        var result = await _globalAuthService.AuthenticateAsync("globaluser", "GlobalPassword123!");

        // Assert - only tenant1 membership should be returned
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Memberships.Count);
        Assert.AreEqual(_tenant1.Id, result.Memberships.First().TenantId);
    }

    [TestMethod]
    public async Task GlobalAuth_FailsWhenAllMembershipsInactive()
    {
        // Arrange - suspend both memberships
        var memberships = await _db.UserTenantMemberships
            .Where(m => m.UserAccountId == _userAccount.Id)
            .ToListAsync();
        foreach (var m in memberships)
        {
            m.Status = TenantMembershipStatus.Suspended;
        }
        await _db.SaveChangesAsync();

        // Act
        var result = await _globalAuthService.AuthenticateAsync("globaluser", "GlobalPassword123!");

        // Assert
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(AuthenticationFailureReason.NoActiveMemberships, result.FailureReason);
    }

    [TestMethod]
    public async Task GlobalLockout_AppliesToAllTenants()
    {
        // Arrange - lock the account
        _userAccount.LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        _userAccount.FailedLoginAttempts = 5;
        await _db.SaveChangesAsync();

        // Act - try to login
        var result = await _globalAuthService.AuthenticateAsync("globaluser", "GlobalPassword123!");

        // Assert - login should fail due to lockout (affects all tenants)
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(AuthenticationFailureReason.AccountLocked, result.FailureReason);
    }

    [TestMethod]
    public async Task FailedAttempts_AccumulateAcrossTenants()
    {
        // Arrange - initial state, no failed attempts
        Assert.AreEqual(0, _userAccount.FailedLoginAttempts);

        // Act - fail authentication multiple times
        await _globalAuthService.AuthenticateAsync("globaluser", "wrong1");
        await _globalAuthService.AuthenticateAsync("global@example.com", "wrong2");
        await _globalAuthService.AuthenticateAsync("globaluser", "wrong3");

        // Assert - all attempts should be accumulated on the same account
        var account = await _db.UserAccounts.FirstAsync(a => a.Id == _userAccount.Id);
        Assert.AreEqual(3, account.FailedLoginAttempts);
    }

    [TestMethod]
    public async Task SuccessfulLogin_ClearsFailedAttempts_Globally()
    {
        // Arrange - set up some failed attempts
        _userAccount.FailedLoginAttempts = 3;
        _userAccount.LastFailedLoginAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _db.SaveChangesAsync();

        // Act - successful login
        var result = await _globalAuthService.AuthenticateAsync("globaluser", "GlobalPassword123!");

        // Assert
        Assert.IsTrue(result.Succeeded);
        var account = await _db.UserAccounts.FirstAsync(a => a.Id == _userAccount.Id);
        Assert.AreEqual(0, account.FailedLoginAttempts);
        Assert.IsNull(account.LastFailedLoginAt);
    }

    private sealed class DummyHasher : IPasswordHasher
    {
        public string Hash(string password) => password;
        public bool Verify(string password, string hash) => hash == password;
    }
}
