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
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class SeedUsageExamples
{
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

        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());
        
        var authCodeExchanger = new AuthorizationCodeExchanger(
            db, jwtSvc, new Mock<IRefreshTokenService>().Object, new Mock<IRevocationService>().Object, 
            options, meta, settingsSvc, entitlementsProvider, tenantsClaimService, pairwiseSubjectService.Object, claimBuilder, 
            lifetimeResolver, opaquePolicy,
            loggerFactory.CreateLogger<AuthorizationCodeExchanger>());

        var refreshTokenExchanger = new RefreshTokenExchanger(
            db, jwtSvc, new Mock<IRefreshTokenService>().Object, new Mock<IRevocationService>().Object,
            options, settingsSvc, entitlementsProvider, tenantsClaimService, pairwiseSubjectService.Object, claimBuilder, 
            lifetimeResolver, opaquePolicy);

        var clientCredentialsFactory = new ClientCredentialsTokenFactory(
            db, jwtSvc, options, settingsSvc, scopeResolver, lifetimeResolver);

        return new TokenService(authCodeExchanger, refreshTokenExchanger, clientCredentialsFactory);
    }

    [TestMethod]
    public async Task Seed_Usage_Example()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using var db = new AuthDbContext(opts);
        var seed = await TestDataSeeder.SeedBasicAsync(db);

        // Issue a code
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var acSvc = new AuthorizationCodeService(db, meta, MockTenantAccessor.CreateWithDefaultTenant(), settingsSvc);
        
        var authorizeResult = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: seed.Clients["spa"].ClientId,
            RedirectUri: "https://app.example.com/callback",
            Scopes: new[] { "openid", "roles" },
            Nonce: "n"
        );
        var (ok, _, _, code) = await acSvc.IssueAsync(authorizeResult, seed.Users["alice"].Id);
        Assert.IsTrue(ok);

        // Exchange it
        var ks = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache());
        var options = Microsoft.Extensions.Options.Options.Create(new AuthOptions());
        var tokenSvc = CreateService(db, TestJwtServiceFactory.Create(ks), options, meta);
        var (ok2, payload, _, status) = await tokenSvc.ExchangeAuthorizationCodeAsync(code!, authorizeResult.RedirectUri!, authorizeResult.ClientId!, "", "https://issuer");
        Assert.IsTrue(ok2);
        Assert.AreEqual(200, status);
    }
}
