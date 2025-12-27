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
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class TokenServiceTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    private static ITokenService CreateService(AuthDbContext db, IJwtService jwtSvc, IOptions<AuthOptions> options, IAuthorizationCodeMetadataStore meta)
    {
        var settingsSvc = new MockTenantSettingsService();
        var scopeResolver = new MockScopeResolver();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();
        var loggerFactory = new LoggerFactory();
        
        var claimBuilder = new AccessTokenClaimBuilder(scopeResolver, options);
        
        var authCodeExchanger = new AuthorizationCodeExchanger(
            db, jwtSvc, new Mock<IRefreshTokenService>().Object, new Mock<IRevocationService>().Object, 
            options, meta, settingsSvc, scopeResolver, entitlementsProvider, tenantsClaimService, claimBuilder, 
            loggerFactory.CreateLogger<AuthorizationCodeExchanger>());

        var refreshTokenExchanger = new RefreshTokenExchanger(
            db, jwtSvc, new Mock<IRefreshTokenService>().Object, new Mock<IRevocationService>().Object,
            options, settingsSvc, scopeResolver, entitlementsProvider, tenantsClaimService, claimBuilder, 
            loggerFactory.CreateLogger<RefreshTokenExchanger>());

        var clientCredentialsFactory = new ClientCredentialsTokenFactory(
            db, jwtSvc, options, settingsSvc, scopeResolver, 
            loggerFactory.CreateLogger<ClientCredentialsTokenFactory>());

        return new TokenService(authCodeExchanger, refreshTokenExchanger, clientCredentialsFactory);
    }

    [TestMethod]
    public async Task ExchangeAuthorizationCodeAsync_Fails_ForInvalidCode()
    {
        using var db = CreateDb();
        var jwtSvc = new Mock<IJwtService>();
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var options = Microsoft.Extensions.Options.Options.Create(new AuthOptions());
        var service = CreateService(db, jwtSvc.Object, options, meta);

        var (ok, payload, error, status) = await service.ExchangeAuthorizationCodeAsync("bad", "https://cb", "c1", "v", "https://issuer");

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
    }
}
