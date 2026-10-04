using System.Security.Claims;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Third 2026-10-04 review: device (and CIBA, which shares the factory) access tokens carried roles from every realm
/// of the tenant while claiming realm=&lt;client realm&gt;. A resource server checking (realm, role) — like ApiService's
/// realm=="admin" &amp;&amp; roles∋"admin" — accepted a role held in an unrelated realm.
/// </summary>
[TestClass]
public sealed class DeviceTokenRealmRolesTests
{
    private static async Task<string[]> IssueRolesAsync(bool systemClient)
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var adminRealm = new Realm { TenantId = tenantId, Name = "admin" };
        var otherRealm = new Realm { TenantId = tenantId, Name = "partners" };
        var client = new MrWhoOidc.Auth.Persistence.Client { TenantId = tenantId, ClientId = "device-app", RealmId = adminRealm.Id, IsSystemClient = systemClient };
        var user = new User { TenantId = tenantId, Username = "mallory", Email = "m@example.com" };
        var ownRole = new Role { TenantId = tenantId, RealmId = adminRealm.Id, Name = "viewer" };
        var foreignRole = new Role { TenantId = tenantId, RealmId = otherRealm.Id, Name = "admin" };
        db.AddRange(adminRealm, otherRealm, client, user, ownRole, foreignRole);
        db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = "roles" }); // R7: scopes must be assigned
        db.UserRealmRoleAssignments.AddRange(
            new UserRealmRoleAssignment { UserId = user.Id, RoleId = ownRole.Id, RealmId = adminRealm.Id },
            new UserRealmRoleAssignment { UserId = user.Id, RoleId = foreignRole.Id, RealmId = otherRealm.Id });
        await db.SaveChangesAsync();

        IEnumerable<Claim>? issued = null;
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, IEnumerable<Claim> claims, DateTimeOffset _, string? _, string? _, DateTimeOffset? _, string? _, CancellationToken _) => issued = claims.ToList())
            .ReturnsAsync("jwt");

        var factory = new DeviceCodeTokenFactory(db, jwt.Object, new MockTenantSettingsService(), new MockScopeResolver(), new TokenLifetimeResolver());
        var (ok, _, error, _) = await factory.CreateTokenAsync(new DeviceCodeTokenRequest("device-app", user.Id, ["openid", "roles"], "api", "https://idp/t/x"));

        Assert.IsTrue(ok, error);
        Assert.AreEqual("admin", issued!.Single(c => c.Type == "realm").Value);
        return issued!.Where(c => c.Type == "roles").Select(c => c.Value).OrderBy(r => r).ToArray();
    }

    [TestMethod]
    public async Task DeviceToken_ForRegularClient_CarriesOnlyTheClientRealmRoles()
    {
        CollectionAssert.AreEqual(new[] { "viewer" }, await IssueRolesAsync(systemClient: false),
            "the 'admin' role from the partners realm must not appear next to realm=admin");
    }

    [TestMethod]
    public async Task DeviceToken_ForTheCliSystemClient_KeepsCrossRealmRoles()
    {
        // The CLI reads platform-admin (held in the platform realm) from its own device-flow token.
        CollectionAssert.AreEqual(new[] { "admin", "viewer" }, await IssueRolesAsync(systemClient: true));
    }
}
