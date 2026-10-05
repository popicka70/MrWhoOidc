using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Device flow and CIBA (which issues through the same factory) emitted the raw internal user id as sub,
/// even for pairwise clients, so the same client saw a different sub than in the code flow and
/// pairwise clients could correlate users across sectors.
/// </summary>
[TestClass]
public sealed class DeviceTokenPairwiseSubjectTests
{
    private static async Task<(string Sub, Guid UserId, AuthDbContext Db)> IssueAsync(string subjectType)
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var realm = new Realm { TenantId = tenantId, Name = "r" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            TenantId = tenantId,
            ClientId = "tv-app",
            RealmId = realm.Id,
            SubjectType = subjectType,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://tv.example.com/cb" })
        };
        var user = new User { TenantId = tenantId, Username = "u", Email = "u@example.com" };
        db.AddRange(realm, client, user);
        await db.SaveChangesAsync();

        IEnumerable<Claim>? issued = null;
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, IEnumerable<Claim> claims, DateTimeOffset _, string? _, string? _, DateTimeOffset? _, string? _, CancellationToken _) => issued = claims.ToList())
            .ReturnsAsync("jwt");

        var pairwise = new PairwiseSubjectService(db, new SectorIdentifierResolver(new Mock<IHttpClientFactory>().Object), NullLogger<PairwiseSubjectService>.Instance);
        var factory = new DeviceCodeTokenFactory(db, jwt.Object, new MockTenantSettingsService(), new MockScopeResolver(), new TokenLifetimeResolver(), pairwise);
        var (ok, _, error, _) = await factory.CreateTokenAsync(new DeviceCodeTokenRequest("tv-app", user.Id, ["openid"], "api", "https://idp/t/x"));

        Assert.IsTrue(ok, error);
        return (issued!.Single(c => c.Type == OidcConstants.Claims.Subject).Value, user.Id, db);
    }

    [TestMethod]
    public async Task DeviceToken_ForPairwiseClient_CarriesPairwiseSub()
    {
        var (sub, userId, db) = await IssueAsync(OidcConstants.SubjectTypes.Pairwise);
        using var _ = db;

        Assert.AreNotEqual(userId.ToString(), sub, "a pairwise client must not receive the internal user id");
        var mapping = await db.PairwiseSubjectIdentifiers.IgnoreQueryFilters().SingleAsync(p => p.UserId == userId);
        Assert.AreEqual(mapping.Subject, sub);
        Assert.AreEqual("tv.example.com", mapping.SectorIdentifier);
    }

    [TestMethod]
    public async Task DeviceToken_ForPublicClient_KeepsUserIdSub()
    {
        var (sub, userId, db) = await IssueAsync(OidcConstants.SubjectTypes.Public);
        using var _ = db;

        Assert.AreEqual(userId.ToString(), sub);
    }
}
