using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class UserServiceTests
{
    private static readonly Guid DefaultTenantId = new("00000000-0000-0000-0000-000000000001");

    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public async Task FindByUsername_ReturnsUser_WhenExists()
    {
        using var db = CreateDb();
        db.Users.Add(new User { Username = "alice", PasswordHash = "h", TenantId = DefaultTenantId });
        await db.SaveChangesAsync();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var svc = new UserService(db, new DummyHasher(), tenantAccessor, new TestHybridCache());
        var u = await svc.FindByUsernameAsync("alice");
        Assert.IsNotNull(u);
        Assert.AreEqual("alice", u!.Username);
    }

    [TestMethod]
    public async Task VerifyPassword_UsesHasher()
    {
        using var db = CreateDb();
        var hasher = new DummyHasher("secret");
        var user = new User { Username = "bob", PasswordHash = hasher.Hash("secret"), TenantId = DefaultTenantId };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var svc = new UserService(db, hasher, tenantAccessor, new TestHybridCache());
        Assert.IsTrue(await svc.VerifyPasswordAsync(user, "secret"));
        Assert.IsFalse(await svc.VerifyPasswordAsync(user, "nope"));
    }

    [TestMethod]
    public async Task FindByUsernameOrEmail_FindsByPrimaryAndAlternativeEmail()
    {
        using var db = CreateDb();
        var u1 = new User { Username = "carol", Email = "carol@example.com", PasswordHash = "h", TenantId = DefaultTenantId };
        var u2 = new User { Username = "dave", PasswordHash = "h", TenantId = DefaultTenantId };
        db.Users.AddRange(u1, u2);
        await db.SaveChangesAsync();
        db.UserAlternativeEmails.Add(new UserAlternativeEmail { UserId = u2.Id, Email = "dave.alt@example.com", IsVerified = true });
        await db.SaveChangesAsync();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var svc = new UserService(db, new DummyHasher(), tenantAccessor, new TestHybridCache());

        var byUser = await svc.FindByUsernameOrEmailAsync("carol");
        Assert.IsNotNull(byUser);
        Assert.AreEqual(u1.Id, byUser!.Id);

        var byPrimaryEmail = await svc.FindByUsernameOrEmailAsync("carol@example.com");
        Assert.IsNotNull(byPrimaryEmail);
        Assert.AreEqual(u1.Id, byPrimaryEmail!.Id);

        var byAlt = await svc.FindByUsernameOrEmailAsync("dave.alt@example.com");
        Assert.IsNotNull(byAlt);
        Assert.AreEqual(u2.Id, byAlt!.Id);

        var missing = await svc.FindByUsernameOrEmailAsync("nobody@example.com");
        Assert.IsNull(missing);
    }

    private sealed class DummyHasher : IPasswordHasher
    {
        private readonly string? _expected;
        public DummyHasher(string? expected = null) => _expected = expected;
        public string Hash(string password) => password;
        public bool Verify(string password, string hash) => (_expected ?? hash) == password;
    }
}
