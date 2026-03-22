using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
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
        var lifetimeResolver = new TokenLifetimeResolver();
        var opaquePolicy = new OpaqueTokenPolicy(options);
        var roleBuilder = new RoleClaimBuilder();

        var claimBuilder = new AccessTokenClaimBuilder(scopeResolver, roleBuilder, options);

        var keyStore = new KeyStore(
            db,
            MockTenantAccessor.CreateWithDefaultTenant(),
                new MrWhoOidc.UnitTests.Helpers.TestHybridCache(),
            Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));

        var keyProvider = TestCachedKeyProviderFactory.Create(keyStore);

        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());

        var authCodeExchanger = new AuthorizationCodeExchanger(
            db, jwtSvc, keyProvider, new Mock<IRefreshTokenService>().Object, new Mock<IRevocationService>().Object,
            options, meta, settingsSvc, entitlementsProvider, tenantsClaimService, pairwiseSubjectService.Object, claimBuilder,
            lifetimeResolver, opaquePolicy,
            loggerFactory.CreateLogger<AuthorizationCodeExchanger>());

        var refreshTokenExchanger = new RefreshTokenExchanger(
            db, jwtSvc, new Mock<IRefreshTokenService>().Object, new Mock<IRevocationService>().Object,
            options, settingsSvc, entitlementsProvider, tenantsClaimService, pairwiseSubjectService.Object, claimBuilder,
            lifetimeResolver, opaquePolicy);

        var clientCredentialsFactory = new ClientCredentialsTokenFactory(
            db, jwtSvc, options, settingsSvc, scopeResolver, lifetimeResolver, NullLogger<ClientCredentialsTokenFactory>.Instance);

        var deviceCodeFactory = new Mock<IDeviceCodeTokenFactory>().Object;

        return new TokenService(authCodeExchanger, refreshTokenExchanger, clientCredentialsFactory, deviceCodeFactory);
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
