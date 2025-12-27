using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Services.Token;

[TestClass]
public sealed class AuthorizationCodeExchangerTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    private static IOptions<AuthOptions> Options(bool opaque = false)
        => Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            ApiAudiences = new[] { "api" },
            OpaqueAccessTokens = new OpaqueAccessTokenOptions { Enabled = opaque }
        });

    [TestMethod]
    public async Task ExchangeAsync_Fails_ForInvalidCode()
    {
        using var db = CreateDb();
        var jwtSvc = new Mock<IJwtService>();
        var refreshSvc = new Mock<IRefreshTokenService>();
        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var scopeResolver = new MockScopeResolver();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();
        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db, jwtSvc.Object, refreshSvc.Object, revocationSvc.Object, Options(), metaStore, settingsSvc, scopeResolver, entitlementsProvider, tenantsClaimService, claimBuilder.Object, logger.Object);

        var request = new AuthorizationCodeExchangeRequest("bad", "https://cb", "c1", "verifier", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
    }

    [TestMethod]
    public async Task ExchangeAsync_Succeeds_JwtAccess()
    {
        using var db = CreateDb();
        var user = new User { Username = "u" };
        var client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1" };
        db.Users.Add(user);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var code = new MrWhoOidc.Auth.Persistence.AuthorizationCode
        {
            Code = "code",
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            UserId = user.Id,
            Nonce = "n",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        db.AuthorizationCodes.Add(code);
        await db.SaveChangesAsync();

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<System.Security.Claims.Claim>>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("fake-jwt");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc.Setup(x => x.CreateRefreshTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("fake-refresh", "fake-hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var scopeResolver = new MockScopeResolver();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();
        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder.Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<System.Security.Claims.Claim>());

        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();
        var exchanger = new AuthorizationCodeExchanger(
            db, jwtSvc.Object, refreshSvc.Object, revocationSvc.Object, Options(), metaStore, settingsSvc, scopeResolver, entitlementsProvider, tenantsClaimService, claimBuilder.Object, logger.Object);

        var request = new AuthorizationCodeExchangeRequest("code", "https://cb", "c1", "", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNotNull(payload);
        var anon = (dynamic)payload;
        Assert.AreEqual("fake-jwt", (string)anon.access_token);
        Assert.AreEqual("fake-refresh", (string)anon.refresh_token);
    }
}
