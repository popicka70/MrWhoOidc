using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class TokenValidatorTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [TestMethod]
    public void Validate_ReturnsPrincipal_ForValidToken()
    {
        using var db = CreateDb();
        var jwt = new JwtService(new KeyStore(db));
        var token = jwt.CreateJwt("https://issuer", "api", new[] { new Claim("sub", "u1") }, DateTimeOffset.UtcNow.AddMinutes(5));
        var validator = new TokenValidator(new KeyStore(db));
        var (ok, principal, _) = validator.Validate(token, "https://issuer");
        Assert.IsTrue(ok);
        Assert.IsNotNull(principal);
        Assert.AreEqual("u1", principal!.FindFirst("sub")?.Value);
    }

    [TestMethod]
    public void Validate_Fails_ForWrongIssuer()
    {
        using var db = CreateDb();
        var jwt = new JwtService(new KeyStore(db));
        var token = jwt.CreateJwt("https://issuer", "api", new[] { new Claim("sub", "u1") }, DateTimeOffset.UtcNow.AddMinutes(5));
        var validator = new TokenValidator(new KeyStore(db));
        var (ok, principal, error) = validator.Validate(token, "https://other");
        Assert.IsFalse(ok);
        Assert.IsNull(principal);
        Assert.IsNotNull(error);
    }
}
