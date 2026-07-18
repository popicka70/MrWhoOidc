using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class KeyStoreTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static KeyStore CreateKeyStore(AuthDbContext db, KeyRotationOptions? opts = null) =>
        new(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(),
            Microsoft.Extensions.Options.Options.Create(opts ?? new KeyRotationOptions()),
            NullLogger<KeyStore>.Instance);

    [TestMethod]
    public async Task GetActiveSigningKey_GeneratesAndPersistsOnce()
    {
        using var db = CreateDb();
        var ks = CreateKeyStore(db);
        var k1 = await ks.GetActiveSigningKeyAsync();
        var k2 = await ks.GetActiveSigningKeyAsync();
        Assert.AreEqual(k1.Kid, k2.Kid);
        Assert.AreEqual(1, await db.SigningKeys.CountAsync());
    }

    [TestMethod]
    public async Task GetPublicJwks_ReturnsPublicPortionOnly()
    {
        using var db = CreateDb();
        var ks = CreateKeyStore(db);
        var _ = await ks.GetActiveSigningKeyAsync();
        var list = await ks.GetPublicJwksAsync();
        Assert.IsGreaterThanOrEqualTo(1, list.Count);
        foreach (var k in list)
        {
            Assert.IsNull(k.D);
            Assert.IsNull(k.P);
            Assert.IsNull(k.Q);
            Assert.IsNull(k.DP);
            Assert.IsNull(k.DQ);
            Assert.IsNull(k.QI);
            Assert.IsFalse(string.IsNullOrEmpty(k.N));
            Assert.IsFalse(string.IsNullOrEmpty(k.E));
        }
    }

    [TestMethod]
    public async Task GetActiveSigningKey_UsesConfiguredRsaSize()
    {
        using var db = CreateDb();
        var ks = CreateKeyStore(db, new KeyRotationOptions { RsaKeySizeBits = 3072 });

        var key = await ks.GetActiveSigningKeyAsync();

        Assert.AreEqual(384, Base64UrlEncoder.DecodeBytes(key.N).Length);
    }
}
