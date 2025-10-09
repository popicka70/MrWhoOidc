using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class ClientAssertionValidatorTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [TestMethod]
    public async Task ValidateAsync_Fails_WhenNoJwks()
    {
        using var db = CreateDb();
        db.Clients.Add(new ClientEntity { ClientId = "c1" });
        await db.SaveChangesAsync();
        var validator = new ClientAssertionValidator(db, new ConfigurationBuilder().Build());
        var (assertion, jwkJson) = CreateClientAssertion("c1", "https://as/connect/token");
        var ok = await validator.ValidateAsync("c1", assertion, "https://as/connect/token");
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task ValidateAsync_Succeeds_WithMatchingJwk()
    {
        using var db = CreateDb();
        var (assertion, jwkJson) = CreateClientAssertion("c1", "https://as/connect/token");
        db.Clients.Add(new ClientEntity { ClientId = "c1", PublicJwksJson = jwkJson });
        await db.SaveChangesAsync();
        var validator = new ClientAssertionValidator(db, new ConfigurationBuilder().Build());
        var ok = await validator.ValidateAsync("c1", assertion, "https://as/connect/token");
        Assert.IsTrue(ok);
    }

    private static (string assertion, string jwkJson) CreateClientAssertion(string clientId, string tokenEndpoint)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var p = rsa.ExportParameters(true);
        var n = Base64UrlEncoder.Encode(p.Modulus);
        var e = Base64UrlEncoder.Encode(p.Exponent);
        var d = Base64UrlEncoder.Encode(p.D);
        var p1 = Base64UrlEncoder.Encode(p.P);
        var q1 = Base64UrlEncoder.Encode(p.Q);
        var dp = Base64UrlEncoder.Encode(p.DP);
        var dq = Base64UrlEncoder.Encode(p.DQ);
        var qi = Base64UrlEncoder.Encode(p.InverseQ);
        var kid = Guid.NewGuid().ToString("N");
        var jwkPrivateJson = $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\",\"d\":\"{d}\",\"p\":\"{p1}\",\"q\":\"{q1}\",\"dp\":\"{dp}\",\"dq\":\"{dq}\",\"qi\":\"{qi}\"}}";
        var creds = new SigningCredentials(new JsonWebKey(jwkPrivateJson), SecurityAlgorithms.RsaSha256);
        var now = DateTime.UtcNow;
        var jti = Guid.NewGuid().ToString("N");
        var token = new JwtSecurityToken(
            issuer: clientId,
            audience: tokenEndpoint,
            claims: new[] { new Claim("sub", clientId), new Claim(JwtRegisteredClaimNames.Jti, jti) },
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(5),
            signingCredentials: creds
        );
        var handler = new JwtSecurityTokenHandler();
        var assertion = handler.WriteToken(token);
        // Return public JWK for validation
        var jwkPublicJson = $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\"}}";
        return (assertion, jwkPublicJson);
    }
}
