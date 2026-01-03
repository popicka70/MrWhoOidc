using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Crypto;
using System.Security.Claims;

using MrWhoOidc.UnitTests.Helpers;
using Microsoft.Extensions.Options;

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
    public async Task CreateJwt_GeneratesToken_AndPersistsKeyOnFirstUse()
    {
        using var db = CreateDb();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Options.Create(new KeyRotationOptions()));
        var svc = TestJwtServiceFactory.Create(keyStore);

        var token = await svc.CreateJwtAsync("https://issuer", "api", new[] { new Claim("sub", "123") }, DateTimeOffset.UtcNow.AddMinutes(5)).ConfigureAwait(false);
        Assert.IsFalse(string.IsNullOrEmpty(token));
        Assert.AreEqual(1, db.SigningKeys.Count());
    }
}
