using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// 2026-10-04 assessment: device-flow (and CIBA, which shares the factory) JWT access tokens were not recorded, so
/// RFC 7009 revocation found nothing to revoke and the jti/hash revocation checks never applied to them.
/// </summary>
[TestClass]
public sealed class IssuedAccessTokenPersistenceTests
{
    [TestMethod]
    public async Task DeviceFlow_AccessToken_Is_Recorded_And_Can_Be_Revoked()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var realm = new Realm { TenantId = tenantId, Name = "main" };
        var client = new ClientEntity { TenantId = tenantId, ClientId = "tv-app", RealmId = realm.Id };
        var user = new User { TenantId = tenantId, Username = "u", Email = "u@example.com" };
        db.AddRange(realm, client, user);
        // R7: scopes must be assigned to the client (default-deny), offline_access included.
        db.ClientScopes.AddRange(new ClientScope { ClientId = client.Id, ScopeName = "openid" }, new ClientScope { ClientId = client.Id, ScopeName = "offline_access" });
        await db.SaveChangesAsync();

        string? jti = null;
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, IEnumerable<Claim> claims, DateTimeOffset _, string? _, string? _, DateTimeOffset? _, string? _, CancellationToken _) => jti = claims.Single(c => c.Type == "jti").Value)
            .ReturnsAsync("device-jwt");

        var factory = new DeviceCodeTokenFactory(db, jwt.Object, new MockTenantSettingsService(), new MockScopeResolver(), new TokenLifetimeResolver(), PublicSubjects());
        var (ok, _, error, _) = await factory.CreateTokenAsync(new DeviceCodeTokenRequest("tv-app", user.Id, ["openid"], "api", "https://idp", DpopJkt: "jkt-1", TenantId: tenantId));
        Assert.IsTrue(ok, error);

        var row = await db.Tokens.SingleAsync(t => t.Type == "access");
        Assert.AreEqual(CryptoHelper.ComputeSha256Base64("device-jwt"), row.TokenHash);
        Assert.AreEqual(jti, row.Jti);
        Assert.AreEqual(user.Id, row.UserId);
        Assert.AreEqual("tv-app", row.ClientId);
        Assert.AreEqual("api", row.Audience);
        Assert.AreEqual("jkt-1", row.CnfJkt);
        Assert.IsTrue(row.ExpiresAt > DateTimeOffset.UtcNow);

        var revocation = new RevocationService(db, MockTenantAccessor.CreateWithDefaultTenant());
        await revocation.RevokeAsync("device-jwt", "access_token", "tv-app");
        Assert.IsNotNull((await db.Tokens.SingleAsync(t => t.Id == row.Id)).RevokedAt);
        Assert.IsNull(row.FamilyId, "no refresh token was issued, so there is no family");
    }

    // RFC 7009 §2.1: with offline_access the device grant issues both tokens; revoking the refresh token revokes the
    // access token issued with it.
    [TestMethod]
    public async Task DeviceFlow_Revoking_The_RefreshToken_Revokes_The_AccessToken_Of_The_Same_Grant()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var realm = new Realm { TenantId = tenantId, Name = "main" };
        var client = new ClientEntity { TenantId = tenantId, ClientId = "tv-app", RealmId = realm.Id };
        var user = new User { TenantId = tenantId, Username = "u", Email = "u@example.com" };
        db.AddRange(realm, client, user);
        // R7: scopes must be assigned to the client (default-deny), offline_access included.
        db.ClientScopes.AddRange(new ClientScope { ClientId = client.Id, ScopeName = "openid" }, new ClientScope { ClientId = client.Id, ScopeName = "offline_access" });
        await db.SaveChangesAsync();

        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("device-jwt-2");
        var factory = new DeviceCodeTokenFactory(db, jwt.Object, new MockTenantSettingsService(), new MockScopeResolver(), new TokenLifetimeResolver(), PublicSubjects());
        var (ok, payload, error, _) = await factory.CreateTokenAsync(new DeviceCodeTokenRequest("tv-app", user.Id, ["openid", "offline_access"], "api", "https://idp", TenantId: tenantId));
        Assert.IsTrue(ok, error);

        var refreshRaw = (string)((Dictionary<string, object?>)payload!)["refresh_token"]!;
        var refreshRow = await db.Tokens.SingleAsync(t => t.Type == "refresh");
        var accessRow = await db.Tokens.SingleAsync(t => t.Type == "access");
        Assert.AreEqual(refreshRow.Id, refreshRow.FamilyId, "the grant's refresh token starts the family");
        Assert.AreEqual(refreshRow.FamilyId, accessRow.FamilyId);

        await new RevocationService(db, MockTenantAccessor.CreateWithDefaultTenant()).RevokeAsync(refreshRaw, "refresh_token", "tv-app");

        Assert.IsNotNull((await db.Tokens.SingleAsync(t => t.Id == accessRow.Id)).RevokedAt);
    }

    private static MrWhoOidc.Auth.Services.SubjectIdentifiers.IPairwiseSubjectService PublicSubjects()
    {
        var subjects = new Mock<MrWhoOidc.Auth.Services.SubjectIdentifiers.IPairwiseSubjectService>();
        subjects.Setup(s => s.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken _) => userId.ToString());
        return subjects.Object;
    }
}
