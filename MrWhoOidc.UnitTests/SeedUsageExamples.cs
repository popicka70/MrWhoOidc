using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Text.Json;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class SeedUsageExamples
{
    [TestMethod]
    public async Task SeedBasic_Allows_Complex_TokenService_Path()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var seed = await TestDataSeeder.SeedBasicAsync(db);

        // Issue an authorization code for alice -> spa with roles scope
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var acSvc = new AuthorizationCodeService(db, meta);
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
        var ks = new KeyStore(db);
        var tokenSvc = new TokenService(db, new JwtService(ks), new RefreshTokenService(db), Microsoft.Extensions.Options.Options.Create(new AuthOptions()), meta, new TokenValidator(ks), null);
        var (ok2, payload, _, status) = await tokenSvc.ExchangeAuthorizationCodeAsync(code!, authorizeResult.RedirectUri!, authorizeResult.ClientId!, "", "https://issuer");
        Assert.IsTrue(ok2);
        Assert.AreEqual(200, status);
    }
}
