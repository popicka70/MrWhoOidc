using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// M-Deact: a grant that was issued while the user was active must not be redeemable after the account
/// was deactivated. The refresh path already enforced this; the authorization-code path and the shared
/// device/CIBA token factory did not, so a pending grant survived the deactivation.
/// </summary>
[TestClass]
public sealed class DeactivatedUserGrantGateTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    private static IOptions<AuthOptions> Options()
        => Microsoft.Extensions.Options.Options.Create(new AuthOptions());

    private static string HashAuthorizationCode(string code)
        => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    [TestMethod]
    public async Task AuthorizationCode_Denied_ForADeactivatedUser()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        var revocationSvc = new Mock<IRevocationService>();
        var settingsSvc = new MockTenantSettingsService();
        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid id, CancellationToken __) => id.ToString());
        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder.Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var keyProvider = new Mock<ICachedKeyProvider>();
        keyProvider.Setup(p => p.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(new byte[32]));

        var exchanger = new AuthorizationCodeExchanger(
            db, jwtSvc.Object, keyProvider.Object, refreshSvc.Object, revocationSvc.Object, Options(), settingsSvc,
            new NoopEntitlementsProvider(), new NoopTenantsClaimService(), pairwiseSubjectService.Object,
            claimBuilder.Object, new TokenLifetimeResolver(), new OpaqueTokenPolicy(Options()),
            NullLogger<AuthorizationCodeExchanger>.Instance);

        db.Realms.Add(new Realm { Id = realmId, Name = "r1", TenantId = tenantId });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId, Status = UserStatus.Deactivated });
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode("code123"),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        var request = new AuthorizationCodeExchangeRequest("code123", "https://cb", "c1", "", "https://issuer", TenantId: tenantId);
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsFalse(ok, "a pending authorization code must not be redeemable after the account was deactivated");
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
        refreshSvc.Verify(r => r.CreateRefreshTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never,
            "no token may be minted for a deactivated user");
    }

    [TestMethod]
    public async Task DeviceCode_Denied_ForADeactivatedUser()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("jwt-at");

        var pairwiseSubjects = new Mock<IPairwiseSubjectService>();
        pairwiseSubjects
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid id, CancellationToken __) => id.ToString());

        var factory = new DeviceCodeTokenFactory(
            db, jwtSvc.Object, new MockTenantSettingsService(), new MockScopeResolver(),
            new TokenLifetimeResolver(), pairwiseSubjects.Object);

        db.Realms.Add(new Realm { Id = realmId, Name = "r1", TenantId = tenantId });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId, Status = UserStatus.Deactivated });
        await db.SaveChangesAsync();

        var request = new DeviceCodeTokenRequest("c1", userId, new[] { "openid" }, "api", "https://issuer", TenantId: tenantId);
        var (ok, _, error, status) = await factory.CreateTokenAsync(request, CancellationToken.None);

        Assert.IsFalse(ok, "a pending device grant must not be redeemable after the account was deactivated");
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
    }

    [TestMethod]
    public async Task AuthorizationCode_Still_Works_ForAnActiveUser()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc.Setup(x => x.CreateRefreshTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(("rt", "hash"));

        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid id, CancellationToken __) => id.ToString());

        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder.Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var keyProvider = new Mock<ICachedKeyProvider>();
        keyProvider.Setup(p => p.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(new byte[32]));

        var exchanger = new AuthorizationCodeExchanger(
            db, jwtSvc.Object, keyProvider.Object, refreshSvc.Object, new Mock<IRevocationService>().Object, Options(),
            new MockTenantSettingsService(), new NoopEntitlementsProvider(), new NoopTenantsClaimService(),
            pairwiseSubjectService.Object, claimBuilder.Object, new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()), NullLogger<AuthorizationCodeExchanger>.Instance);

        db.Realms.Add(new Realm { Id = realmId, Name = "r1", TenantId = tenantId });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId, Status = UserStatus.Active });
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode("code123"),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        var request = new AuthorizationCodeExchangeRequest("code123", "https://cb", "c1", "", "https://issuer", TenantId: tenantId);
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok, $"an active user must still be able to redeem a code (error: {error})");
        Assert.AreEqual(200, status);
    }
}
