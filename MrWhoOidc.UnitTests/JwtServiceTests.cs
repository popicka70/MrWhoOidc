using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Crypto;
using System.Security.Claims;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class JwtServiceTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public void CreateJwt_GeneratesToken_AndPersistsKeyOnFirstUse()
    {
        using var db = CreateDb();
        var keyStore = new KeyStore(db);
        var svc = new JwtService(keyStore);

        var token = svc.CreateJwt("https://issuer", "api", new[] { new Claim("sub", "123") }, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.IsFalse(string.IsNullOrEmpty(token));
        Assert.AreEqual(1, db.SigningKeys.Count());
    }
}
