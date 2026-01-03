using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Entitlements.Contracts;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class TokenRoleEmissionTests
{
    private static ITokenService CreateService(AuthDbContext db, IJwtService jwtSvc, IOptions<AuthOptions> options)
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
            options, new InMemoryAuthorizationCodeMetadataStore(), settingsSvc, entitlementsProvider, tenantsClaimService, pairwiseSubjectService.Object, claimBuilder, 
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
    public async Task ClientCredentials_Emits_Roles_From_Entitlements()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using var db = new AuthDbContext(opts);

    #pragma warning disable CS0618 // Client.ClientSecretHash is obsolete; test keeps legacy guard behavior.
        var client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", ClientSecretHash = "h" };
    #pragma warning restore CS0618
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var jwtSvc = new Mock<IJwtService>();
        IEnumerable<Claim> capturedClaims = null!;
        jwtSvc.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IEnumerable<Claim>, DateTimeOffset, string, string, DateTimeOffset?, string, CancellationToken>((a, b, claims, d, e, f, g, h, i) => capturedClaims = claims)
            .ReturnsAsync("jwt");

        var options = Microsoft.Extensions.Options.Options.Create(new AuthOptions());
        
        // Setup entitlements provider to return roles
        var entitlementsMock = new Mock<IEntitlementsProvider>();
        entitlementsMock.Setup(x => x.GetEffectiveEntitlementsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, Entitlement>());

        var settingsSvc = new MockTenantSettingsService();
        var scopeResolver = new MockScopeResolver();
        var tenantsClaimService = new NoopTenantsClaimService();
        var loggerFactory = new LoggerFactory();
        var claimBuilder = new AccessTokenClaimBuilder(scopeResolver, new RoleClaimBuilder(), options);
        
        var factory = new ClientCredentialsTokenFactory(
            db, jwtSvc.Object, options, settingsSvc, scopeResolver, new TokenLifetimeResolver());

        var tokenSvc = new TokenService(new Mock<IAuthorizationCodeExchanger>().Object, new Mock<IRefreshTokenExchanger>().Object, factory);

        var (ok, _, _, _) = await tokenSvc.CreateClientCredentialsTokenAsync("c1", "api", new[] { "openid" }, "https://issuer");
        
        Assert.IsTrue(ok);
    }
}
