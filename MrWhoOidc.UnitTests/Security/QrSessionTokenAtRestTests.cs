using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Infrastructure.Security;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// M-Plain (remainder): the QR session token was stored in plaintext next to its hash. The token travels in the QR
/// code and in the initiating browser's cookie, so a database dump handed out a live QR session. Only the hash is
/// stored now; every lookup and the initiator cookie name are derived from it.
/// </summary>
[TestClass]
public sealed class QrSessionTokenAtRestTests
{
    private static (QrLoginService service, AuthDbContext db) CreateService()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var tenants = MockTenantAccessor.CreateWithDefaultTenant();
        var service = new QrLoginService(db, Options.Create(new QrLoginOptions { BaseUrl = "https://idp.example" }), tenants);
        return (service, db);
    }

    private static Task<QrSessionCreated> CreateAsync(QrLoginService service) =>
        service.CreateSessionAsync("app", "/", "challenge", "S256", "st", null, "openid",
            new QrInitiatorInfo("203.0.113.7", "test-agent"));

    [TestMethod]
    public async Task TheSessionToken_IsNotStored_OnlyItsHash()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var created = await CreateAsync(service);

        var stored = await db.QrLoginSessions.AsNoTracking().SingleAsync();
        Assert.IsNull(stored.SessionToken, "the session token must not be stored in plaintext");
        Assert.AreEqual(CryptoHelper.ComputeSha256Hex(created.SessionToken), stored.SessionTokenHash);
        Assert.IsFalse((stored.SessionToken ?? string.Empty).Contains(created.SessionToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ASession_IsFoundByItsToken()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var created = await CreateAsync(service);

        var found = await service.GetSessionAsync(created.SessionToken);
        Assert.IsNotNull(found);
        Assert.AreEqual(CryptoHelper.ComputeSha256Hex(created.SessionToken), found!.SessionTokenHash);
    }

    [TestMethod]
    public async Task ALegacyRowThatStillCarriesThePlaintextToken_IsFound()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var created = await CreateAsync(service);
        var stored = await db.QrLoginSessions.SingleAsync();
        stored.SessionToken = created.SessionToken;
        stored.SessionTokenHash = null;
        await db.SaveChangesAsync();

        var found = await service.GetSessionAsync(created.SessionToken);
        Assert.IsNotNull(found, "a row written before the token was hashed must still be found");
    }

    [TestMethod]
    public void TheInitiatorCookieName_IsTheSame_WhetherItIsDerivedFromTheTokenOrFromItsHash()
    {
        const string token = "qr-session-token";
        var hash = CryptoHelper.ComputeSha256Hex(token);

        Assert.AreEqual(QrInitiatorBinding.CookieName(token), QrInitiatorBinding.CookieNameFromHash(hash));
    }

    [TestMethod]
    public async Task TheInitiatorBinding_StillWorks_WhenTheRowHasNoPlaintextToken()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var created = await CreateAsync(service);
        var stored = await db.QrLoginSessions.AsNoTracking().SingleAsync();

        var http = new DefaultHttpContext();
        QrInitiatorBinding.Issue(http, created.SessionToken, created.InitiatorSecret, created.ExpiresAt);
        var cookie = http.Response.Headers.SetCookie.ToString();
        var pair = cookie.Split(';')[0];
        http.Request.Headers.Cookie = pair;

        Assert.IsTrue(QrInitiatorBinding.IsBound(http, stored),
            "the initiating browser must still be recognized without the plaintext token");
    }
}
