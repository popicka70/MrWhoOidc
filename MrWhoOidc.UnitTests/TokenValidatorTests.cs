using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
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
}


