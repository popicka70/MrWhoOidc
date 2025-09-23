using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class UserServiceTests
{
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
        db.Users.Add(new User { Username = "alice", PasswordHash = "h" });
        await db.SaveChangesAsync();
        var svc = new UserService(db, new DummyHasher());
        var u = await svc.FindByUsernameAsync("alice");
        Assert.IsNotNull(u);
        Assert.AreEqual("alice", u!.Username);
    }

    [TestMethod]
    public async Task VerifyPassword_UsesHasher()
    {
        using var db = CreateDb();
        var hasher = new DummyHasher("secret");
        var user = new User { Username = "bob", PasswordHash = hasher.Hash("secret") };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var svc = new UserService(db, hasher);
        Assert.IsTrue(await svc.VerifyPasswordAsync(user, "secret"));
        Assert.IsFalse(await svc.VerifyPasswordAsync(user, "nope"));
    }

    private sealed class DummyHasher : IPasswordHasher
    {
        private readonly string? _expected;
        public DummyHasher(string? expected = null) => _expected = expected;
        public string Hash(string password) => password;
        public bool Verify(string password, string hash) => (_expected ?? hash) == password;
    }
}
