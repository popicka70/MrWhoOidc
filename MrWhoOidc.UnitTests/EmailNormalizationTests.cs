using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class EmailNormalizationTests
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
    public async Task SaveChanges_NormalizesUserEmails()
    {
        using var db = CreateDb();
        db.Users.Add(new User
        {
            Username = "u1",
            PasswordHash = "hash",
            Email = " Mixed.Case@Example.COM "
        });

        await db.SaveChangesAsync();

        var user = await db.Users.AsNoTracking().SingleAsync();
        Assert.AreEqual("Mixed.Case@Example.COM", user.Email);
        Assert.AreEqual("mixed.case@example.com", user.NormalizedEmail);
    }

    [TestMethod]
    public async Task SaveChanges_NormalizesAlternativeEmails()
    {
        using var db = CreateDb();
        var user = new User { Username = "u2", PasswordHash = "hash" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.UserAlternativeEmails.Add(new UserAlternativeEmail
        {
            UserId = user.Id,
            Email = " Alt@Example.COM ",
            IsVerified = true
        });

        await db.SaveChangesAsync();

        var alt = await db.UserAlternativeEmails.AsNoTracking().SingleAsync();
        Assert.AreEqual("Alt@Example.COM", alt.Email);
        Assert.AreEqual("alt@example.com", alt.NormalizedEmail);
    }

    [TestMethod]
    public async Task SaveChanges_NormalizesRegistrations()
    {
        using var db = CreateDb();
        db.Registrations.Add(new Registration
        {
            Email = " Pending@Example.COM ",
            State = "pending"
        });

        await db.SaveChangesAsync();

        var reg = await db.Registrations.AsNoTracking().SingleAsync();
        Assert.AreEqual("Pending@Example.COM", reg.Email);
        Assert.AreEqual("pending@example.com", reg.NormalizedEmail);
    }

    [TestMethod]
    public async Task SaveChanges_InvalidEmailThrows()
    {
        using var db = CreateDb();
        db.Users.Add(new User
        {
            Username = "u3",
            PasswordHash = "hash",
            Email = "not-an-email"
        });

        await Assert.ThrowsExactlyAsync<ValidationException>(() => db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task UserService_FindsByNormalizedEmail()
    {
        using var db = CreateDb();
        var user = new User
        {
            Username = "u4",
            PasswordHash = "hash",
            Email = "Lookup@Example.COM",
            TenantId = DefaultTenantId
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.UserAlternativeEmails.Add(new UserAlternativeEmail
        {
            UserId = user.Id,
            Email = "Alias@Example.COM",
            IsVerified = true
        });
        await db.SaveChangesAsync();

        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var service = new UserService(db, new NoopHasher(), tenantAccessor);

        var byPrimary = await service.FindByUsernameOrEmailAsync("lookup@example.com");
        Assert.IsNotNull(byPrimary);
        Assert.AreEqual(user.Id, byPrimary!.Id);

        var byAlt = await service.FindByUsernameOrEmailAsync("alias@example.com");
        Assert.IsNotNull(byAlt);
        Assert.AreEqual(user.Id, byAlt!.Id);
    }

    private sealed class NoopHasher : IPasswordHasher
    {
        public string Hash(string password) => password;
        public bool Verify(string password, string hash) => password == hash;
    }
}
