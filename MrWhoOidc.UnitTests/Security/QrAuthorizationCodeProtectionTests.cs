using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// M-Plain: the QR session's authorization code is a live credential (it is exchanged for tokens) and must not
/// sit in the database in plaintext. It has to be handed back to the browser, so it is protected reversibly.
/// </summary>
[TestClass]
public sealed class QrAuthorizationCodeProtectionTests
{
    private sealed class TestSecretProtector(IServiceProvider provider) : ISecretProtector
    {
        private readonly IDataProtector _protector = provider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("MrWhoOidc.Auth.IdentityProviders.ConfigSecret.v1");

        public string ProtectSigningKeyJwk(string plaintext) => plaintext;
        public string UnprotectSigningKeyJwk(string storedValue) => storedValue;
        public string ProtectTotpSecret(string plaintext) => plaintext;
        public string? UnprotectTotpSecret(string? storedValue) => storedValue;
        public string ProtectProviderSecret(string plaintext) => "dp:v1:" + _protector.Protect(plaintext);
        public string UnprotectProviderSecret(string storedValue)
            => storedValue.StartsWith("dp:v1:", StringComparison.Ordinal)
                ? _protector.Unprotect(storedValue["dp:v1:".Length..])
                : storedValue;
        public string ProtectDeviceUserCode(string plaintext) => "dp:v1:" + _protector.Protect(plaintext);
        public string UnprotectDeviceUserCode(string storedValue)
            => storedValue.StartsWith("dp:v1:", StringComparison.Ordinal)
                ? _protector.Unprotect(storedValue["dp:v1:".Length..])
                : storedValue;
        public bool IsProtected(string? storedValue) => storedValue?.StartsWith("dp:v1:", StringComparison.Ordinal) == true;
    }

    private static (QrLoginService service, AuthDbContext db, TenantAccessor tenants) CreateService()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDataProtection();
        services.AddLogging();
        var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
            .BuildServiceProvider(services);

        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AuthDbContext(options);
        var tenants = new TenantAccessor();
        tenants.SetTenant(new TenantContext { TenantId = Guid.NewGuid(), Slug = "default", Name = "Default" });

        var service = new QrLoginService(
            db,
            Options.Create(new QrLoginOptions { BaseUrl = "https://idp.example" }),
            tenants,
            new TestSecretProtector(provider));

        return (service, db, tenants);
    }

    [TestMethod]
    public async Task AuthorizationCode_IsStoredProtected_AndReturnedInPlaintext()
    {
        var (service, db, _) = CreateService();
        await using var _db = db;

        var created = await service.CreateSessionAsync(
            "client-1",
            "https://rp.example/cb",
            new string('a', 43),
            "S256",
            "state",
            null,
            "openid",
            new QrInitiatorInfo("203.0.113.1", "test-agent"));

        const string authCode = "the-live-authorization-code";
        var updated = await service.UpdateStatusAsync(
            created.SessionToken, QrSessionStatus.Authenticated, Guid.NewGuid(), authCode);
        Assert.IsTrue(updated);

        var stored = await db.QrLoginSessions.AsNoTracking().SingleAsync();
        Assert.AreNotEqual(authCode, stored.AuthorizationCode, "the authorization code must not be stored in plaintext");
        Assert.IsTrue(stored.AuthorizationCode!.StartsWith("dp:v1:", StringComparison.Ordinal),
            "the authorization code must be protected at rest");
        Assert.IsFalse(stored.AuthorizationCode.Contains(authCode, StringComparison.Ordinal),
            "the plaintext code must not appear in the stored value");

        Assert.AreEqual(authCode, service.GetAuthorizationCode(stored),
            "the code must be handed back to the browser unchanged");
    }

    [TestMethod]
    public async Task AuthorizationCode_IsNotDoubleProtected_WhenTheSessionIsConsumed()
    {
        var (service, db, _) = CreateService();
        await using var _db = db;

        var created = await service.CreateSessionAsync(
            "client-1",
            "https://rp.example/cb",
            new string('a', 43),
            "S256",
            "state",
            null,
            "openid",
            new QrInitiatorInfo("203.0.113.1", "test-agent"));

        const string authCode = "the-live-authorization-code";
        await service.UpdateStatusAsync(created.SessionToken, QrSessionStatus.Authenticated, Guid.NewGuid(), authCode);

        var session = await service.GetSessionAsync(created.SessionToken);
        Assert.IsNotNull(session);

        // The consumed transition re-stores the same code.
        await service.UpdateStatusAsync(
            created.SessionToken, QrSessionStatus.Consumed, session!.UserId, service.GetAuthorizationCode(session));

        var stored = await db.QrLoginSessions.AsNoTracking().SingleAsync();
        Assert.AreEqual(authCode, service.GetAuthorizationCode(stored));
        Assert.IsTrue(stored.AuthorizationCode!.StartsWith("dp:v1:", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ALegacyPlaintextCode_IsStillReturnedUnchanged()
    {
        var (service, db, _) = CreateService();
        await using var _db = db;

        var created = await service.CreateSessionAsync(
            "client-1",
            "https://rp.example/cb",
            new string('a', 43),
            "S256",
            "state",
            null,
            "openid",
            new QrInitiatorInfo("203.0.113.1", "test-agent"));

        var session = await service.GetSessionAsync(created.SessionToken);
        Assert.IsNotNull(session);
        session!.AuthorizationCode = "legacy-plaintext-code";

        Assert.AreEqual("legacy-plaintext-code", service.GetAuthorizationCode(session),
            "a row that was stored before the protection must still work");
    }
}
