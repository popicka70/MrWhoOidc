using System.Security.Claims;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// ADR-0010 (H3): admin API tokens (aud urn:mrwho:admin-api, scope mrwho:admin) are issued only to designated admin
/// clients (system clients with AllowAdminApi), whatever ApiAudiences, a client's own allow-list or its scope
/// assignments say.
/// </summary>
[TestClass]
public sealed class AdminApiIssuanceTests
{
    private static MrWhoOidc.Auth.Persistence.Client NewClient(bool system, bool allowAdminApi, string? m2mAudiences = null) => new()
    {
        TenantId = Guid.NewGuid(),
        ClientId = system ? "mrwho-cli-acme" : "tenant-app",
        IsSystemClient = system,
        AllowAdminApi = allowAdminApi,
        M2MAllowedAudiencesJson = m2mAudiences,
    };

    [TestMethod]
    [DataRow(false, false, false, DisplayName = "ordinary client")]
    [DataRow(false, true, false, DisplayName = "AllowAdminApi on a non-system client")]
    [DataRow(true, false, false, DisplayName = "system client without AllowAdminApi")]
    [DataRow(true, true, true, DisplayName = "CLI client")]
    public void AdminResource_OnlyForAdminClients(bool system, bool allowAdminApi, bool expected)
    {
        // Listing the admin resource in ApiAudiences or the client's own allow-list must not open it up.
        var client = NewClient(system, allowAdminApi, m2mAudiences: $"[\"{AdminApiAccess.Resource}\"]");

        Assert.AreEqual(expected, ResourceIndicatorPolicy.IsAllowed(client, ["api", AdminApiAccess.Resource], AdminApiAccess.Resource));
    }

    [TestMethod]
    [DataRow(false, DisplayName = "tenant app with the scope assigned")]
    [DataRow(true, DisplayName = "CLI client")]
    public async Task DeviceToken_AdminScope_OnlyForAdminClients(bool adminClient)
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var realm = new Realm { Name = "default" };
        var client = NewClient(system: adminClient, allowAdminApi: adminClient);
        realm.TenantId = client.TenantId;
        client.RealmId = realm.Id;
        var user = new User { TenantId = client.TenantId, Username = "admin", Email = "admin@example.com" };
        db.AddRange(realm, client, user);
        // A tenant admin can assign any scope name to their own client.
        db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = AdminApiAccess.Scope });
        await db.SaveChangesAsync();

        string? scope = null;
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, IEnumerable<Claim> claims, DateTimeOffset _, string? _, string? _, DateTimeOffset? _, string? _, CancellationToken _)
                => scope = claims.FirstOrDefault(c => c.Type == "scope")?.Value)
            .ReturnsAsync("jwt");

        var factory = new DeviceCodeTokenFactory(db, jwt.Object, new MockTenantSettingsService(), new MockScopeResolver(), new TokenLifetimeResolver(),
            new MrWhoOidc.Auth.Services.SubjectIdentifiers.PairwiseSubjectService(db, new MrWhoOidc.Auth.Services.SubjectIdentifiers.SectorIdentifierResolver(new Mock<IHttpClientFactory>().Object), Microsoft.Extensions.Logging.Abstractions.NullLogger<MrWhoOidc.Auth.Services.SubjectIdentifiers.PairwiseSubjectService>.Instance));
        var (ok, _, error, _) = await factory.CreateTokenAsync(new DeviceCodeTokenRequest(client.ClientId, user.Id, ["openid", AdminApiAccess.Scope], "api", "https://idp/t/acme"));

        Assert.IsTrue(ok, error);
        Assert.AreEqual(adminClient, (scope ?? string.Empty).Split(' ').Contains(AdminApiAccess.Scope));
    }
}
