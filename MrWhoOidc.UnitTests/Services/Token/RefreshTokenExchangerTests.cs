using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Services.Token;

[TestClass]
public sealed class RefreshTokenExchangerTests
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

    [TestMethod]
    public async Task ExchangeAsync_Fails_ForInvalidToken()
    {
        using var db = CreateDb();
        var jwtSvc = new Mock<IJwtService>();
        var refreshSvc = new Mock<IRefreshTokenService>();
        var revocationSvc = new Mock<IRevocationService>();
        var settingsSvc = new MockTenantSettingsService();
        var scopeResolver = new MockScopeResolver();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();
        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        var logger = new Mock<ILogger<RefreshTokenExchanger>>();

        var exchanger = new RefreshTokenExchanger(db, jwtSvc.Object, refreshSvc.Object, revocationSvc.Object, Options(), settingsSvc, scopeResolver, entitlementsProvider, tenantsClaimService, claimBuilder.Object, new TokenLifetimeResolver(), new OpaqueTokenPolicy(Options()), logger.Object);

        var request = new RefreshTokenExchangeRequest("bad", "c1", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
    }

    [TestMethod]
    public async Task ExchangeAsync_Succeeds()
    {
        using var db = CreateDb();
        var user = new User { Username = "u" };
        var client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1" };
        db.Users.Add(user);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var rt = new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "refresh",
            TokenHash = CryptoHelper.ComputeSha256Base64("rt"),
            ClientId = "c1",
            UserId = user.Id,
            ScopesJson = "[\"openid\"]",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        db.Tokens.Add(rt);
        await db.SaveChangesAsync();

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<System.Security.Claims.Claim>>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("fake-jwt");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc.Setup(x => x.CreateRefreshTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-rt", "new-h"));

        var revocationSvc = new Mock<IRevocationService>();
        var settingsSvc = new MockTenantSettingsService();
        var scopeResolver = new MockScopeResolver();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();
        
        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder.Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<System.Security.Claims.Claim>());

        var logger = new Mock<ILogger<RefreshTokenExchanger>>();
        var exchanger = new RefreshTokenExchanger(db, jwtSvc.Object, refreshSvc.Object, revocationSvc.Object, Options(), settingsSvc, scopeResolver, entitlementsProvider, tenantsClaimService, claimBuilder.Object, new TokenLifetimeResolver(), new OpaqueTokenPolicy(Options()), logger.Object);

        var request = new RefreshTokenExchangeRequest("rt", "c1", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNotNull(payload);
        var anon = (dynamic)payload;
        Assert.AreEqual("fake-jwt", (string)anon.access_token);
        Assert.AreEqual("new-rt", (string)anon.refresh_token);
    }
}


