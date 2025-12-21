using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Text.Json;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class SeedUsageExamples
{
    [TestMethod]
    public async Task SeedBasic_Allows_Complex_TokenService_Path()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var settingsService = new MockTenantSettingsService();
        var seed = await TestDataSeeder.SeedBasicAsync(db);

        // Issue an authorization code for alice -> spa with roles scope
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var acSvc = new AuthorizationCodeService(db, meta, MockTenantAccessor.CreateWithDefaultTenant(), settingsService);
        var authorizeResult = new MrWhoOidc.Auth.Protocols.AuthorizeValidationResult
        {
            IsValid = true,
            ClientId = seed.Clients["spa"].ClientId,
            RedirectUri = "https://app.example.com/callback",
            Scopes = new[] { "openid", "roles" },
            Nonce = "n",
            CodeChallenge = null
        };
        var (ok, _, _, code) = await acSvc.IssueAsync(authorizeResult, seed.Users["alice"].Id);
        Assert.IsTrue(ok);

        // Exchange it
        var ks = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache());
        var scopeResolver = new MockScopeResolver();
        var tokenSvc = new TokenService(db, new JwtService(ks), new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant(), settingsService), Microsoft.Extensions.Options.Options.Create(new AuthOptions()), meta, settingsService, scopeResolver, new NoopEntitlementsProvider());
        var (ok2, payload, _, status) = await tokenSvc.ExchangeAuthorizationCodeAsync(code!, authorizeResult.RedirectUri!, authorizeResult.ClientId!, "", "https://issuer");
        Assert.IsTrue(ok2);
        Assert.AreEqual(200, status);
    }
}
