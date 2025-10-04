using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class TokenRoleEmissionTests
{
    [TestMethod]
    public async Task AuthorizationCodeExchange_OmitsRolesWhenScopeMissing()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var seed = await TestDataSeeder.SeedBasicAsync(db);

        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var codeSvc = new AuthorizationCodeService(db, meta, MockTenantAccessor.CreateWithDefaultTenant());
        var request = new AuthorizeValidationResult
        {
            IsValid = true,
            ClientId = seed.Clients["spa"].ClientId,
            RedirectUri = "https://app.example.com/callback",
            Scopes = new[] { "openid", "profile" },
            Nonce = "n"
        };
        var (_, _, _, code) = await codeSvc.IssueAsync(request, seed.Users["alice"].Id);

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var tokenSvc = new TokenService(
            db,
            new JwtService(keyStore),
            new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant()),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            meta,
            new TokenValidator(keyStore),
            null);

        var (ok, payload, _, _) = await tokenSvc.ExchangeAuthorizationCodeAsync(code!, request.RedirectUri!, request.ClientId!, string.Empty, "https://issuer");
        Assert.IsTrue(ok);

        var handler = new JwtSecurityTokenHandler();
        var tokens = (dynamic)payload!;
        var idTokenClaims = handler.ReadJwtToken((string)tokens.id_token).Claims.ToList();
        var accessTokenClaims = handler.ReadJwtToken((string)tokens.access_token).Claims.ToList();

        Assert.IsFalse(idTokenClaims.Any(c => c.Type == "roles"), "ID token should not contain roles without the roles scope.");
        Assert.IsFalse(accessTokenClaims.Any(c => c.Type == "roles"), "Access token should not contain roles without the roles scope.");
    }

    [TestMethod]
    public async Task AuthorizationCodeExchange_OmitsRolesWhenNoAssignments()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var seed = await TestDataSeeder.SeedBasicAsync(db);

        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var codeSvc = new AuthorizationCodeService(db, meta, MockTenantAccessor.CreateWithDefaultTenant());
        var request = new AuthorizeValidationResult
        {
            IsValid = true,
            ClientId = seed.Clients["spa"].ClientId,
            RedirectUri = "https://app.example.com/callback",
            Scopes = new[] { "openid", "roles" },
            Nonce = "n"
        };
        var (_, _, _, code) = await codeSvc.IssueAsync(request, seed.Users["bob"].Id);

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var tokenSvc = new TokenService(
            db,
            new JwtService(keyStore),
            new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant()),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            meta,
            new TokenValidator(keyStore),
            null);

        var (ok, payload, _, _) = await tokenSvc.ExchangeAuthorizationCodeAsync(code!, request.RedirectUri!, request.ClientId!, string.Empty, "https://issuer");
        Assert.IsTrue(ok);

        var handler = new JwtSecurityTokenHandler();
        var tokens = (dynamic)payload!;
        var idRoles = handler.ReadJwtToken((string)tokens.id_token).Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();
        var accessRoles = handler.ReadJwtToken((string)tokens.access_token).Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();

        CollectionAssert.AreEqual(Array.Empty<string>(), idRoles, "ID token should omit roles when user lacks assignments.");
        CollectionAssert.AreEqual(Array.Empty<string>(), accessRoles, "Access token should omit roles when user lacks assignments.");
    }

    [TestMethod]
    public async Task AuthorizationCodeExchange_EmitsRolesWhenScopeAndAssignmentsPresent()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var seed = await TestDataSeeder.SeedBasicAsync(db);

        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var codeSvc = new AuthorizationCodeService(db, meta, MockTenantAccessor.CreateWithDefaultTenant());
        var request = new AuthorizeValidationResult
        {
            IsValid = true,
            ClientId = seed.Clients["spa"].ClientId,
            RedirectUri = "https://app.example.com/callback",
            Scopes = new[] { "openid", "roles" },
            Nonce = "n"
        };
        var (_, _, _, code) = await codeSvc.IssueAsync(request, seed.Users["alice"].Id);

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var tokenSvc = new TokenService(
            db,
            new JwtService(keyStore),
            new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant()),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            meta,
            new TokenValidator(keyStore),
            null);

        var (ok, payload, _, _) = await tokenSvc.ExchangeAuthorizationCodeAsync(code!, request.RedirectUri!, request.ClientId!, string.Empty, "https://issuer");
        Assert.IsTrue(ok);

        var handler = new JwtSecurityTokenHandler();
        var tokens = (dynamic)payload!;
        var idRoles = handler.ReadJwtToken((string)tokens.id_token).Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();
        var accessRoles = handler.ReadJwtToken((string)tokens.access_token).Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();

        var expectedRoles = new[] { "admin", "user" };
        CollectionAssert.AreEquivalent(expectedRoles, idRoles);
        CollectionAssert.AreEquivalent(expectedRoles, accessRoles);
    }
}
