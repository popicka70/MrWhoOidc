using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class TokenValidatorTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [TestMethod]
    public async Task Validate_ReturnsPrincipal_ForValidToken()
    {
        using var db = CreateDb();
        var ks = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(ks);
        var token = await jwt.CreateJwtAsync("https://issuer", "api", new[] { new Claim("sub", "u1") }, DateTimeOffset.UtcNow.AddMinutes(5)).ConfigureAwait(false);
        var validator = TestTokenValidatorFactory.Create(ks);
        var (ok, principal, _) = await validator.ValidateAsync(token, "https://issuer");
        Assert.IsTrue(ok);
        Assert.IsNotNull(principal);
        Assert.AreEqual("u1", principal!.FindFirst("sub")?.Value);
    }

    [TestMethod]
    public async Task Validate_Fails_ForWrongIssuer()
    {
        using var db = CreateDb();
        var ks = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(ks);
        var token = await jwt.CreateJwtAsync("https://issuer", "api", new[] { new Claim("sub", "u1") }, DateTimeOffset.UtcNow.AddMinutes(5)).ConfigureAwait(false);
        var validator = TestTokenValidatorFactory.Create(ks);
        var (ok, principal, error) = await validator.ValidateAsync(token, "https://other");
        Assert.IsFalse(ok);
        Assert.IsNull(principal);
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public async Task Validate_Fails_ForWrongAudience_WhenExpectedAudienceProvided()
    {
        using var db = CreateDb();
        var ks = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(ks);
        var token = await jwt.CreateJwtAsync("https://issuer", "api-a", new[] { new Claim("sub", "u1") }, DateTimeOffset.UtcNow.AddMinutes(5)).ConfigureAwait(false);
        var validator = TestTokenValidatorFactory.Create(ks);

        var (ok, principal, error) = await validator.ValidateAsync(token, "https://issuer", validAudiences: ["api-b"]);

        Assert.IsFalse(ok);
        Assert.IsNull(principal);
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public async Task Validate_Fails_ForRevokedPersistedAccessToken()
    {
        using var db = CreateDb();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var ks = new KeyStore(db, tenantAccessor, new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(ks);

        var userId = Guid.NewGuid();
        var token = await jwt.CreateJwtAsync(
            "https://issuer",
            "api",
            new[]
            {
                new Claim("sub", userId.ToString()),
                new Claim("scope", "openid"),
                new Claim("jti", "revoked-jti")
            },
            DateTimeOffset.UtcNow.AddMinutes(5),
            tokenType: SecurityConstants.JwtTokenTypes.AtJwt).ConfigureAwait(false);

        db.Tokens.Add(new Token
        {
            TenantId = tenantAccessor.CurrentTenant!.TenantId,
            Type = "access",
            TokenHash = CryptoHelper.ComputeSha256Base64(token),
            UserId = userId,
            ClientId = "c1",
            ScopesJson = "[\"openid\"]",
            Audience = "api",
            Jti = "revoked-jti",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var validator = TestTokenValidatorFactory.Create(ks, db, tenantAccessor);
        var (ok, principal, error) = await validator.ValidateAsync(token, "https://issuer");

        Assert.IsFalse(ok);
        Assert.IsNull(principal);
        Assert.AreEqual("token_revoked", error);
    }
}


