using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Moq;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.UnitTests.TestSupport;

namespace MrWhoOidc.UnitTests.Services.Token;

[TestClass]
public sealed class AccessTokenJsonClaimTests
{
    [TestMethod]
    public async Task AccessToken_Serializes_Cnf_As_Object_And_Emits_ClientId()
    {
        var signingKey = SharedTestKeys.GetRsaSecurityKey();
        var keyProvider = new Mock<ICachedKeyProvider>();
        keyProvider.Setup(p => p.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(signingKey);
        var jwtService = new JwtService(keyProvider.Object);

        var builder = new AccessTokenClaimBuilder(new MockScopeResolver(), new RoleClaimBuilder(), Microsoft.Extensions.Options.Options.Create(new AuthOptions()));
        var claims = await builder.BuildClaimsAsync(new AccessTokenClaimRequest(Guid.NewGuid(), "c1", new[] { "openid" }, "https://issuer", DpopJkt: "jkt-123"));

        var token = await jwtService.CreateJwtAsync("https://issuer", "api", claims, DateTimeOffset.UtcNow.AddMinutes(5), tokenType: "at+jwt");

        // Inspect the raw payload: RFC 9449 §6.1 / RFC 7800 require cnf to be a JSON object, not a string.
        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(token.Split('.')[1]));
        var cnf = payload.RootElement.GetProperty("cnf");
        Assert.AreEqual(JsonValueKind.Object, cnf.ValueKind, $"cnf must be an object; payload={payload.RootElement}");
        Assert.AreEqual("jkt-123", cnf.GetProperty("jkt").GetString());
        // RFC 9068 §2.2: client_id is required in JWT access tokens.
        Assert.AreEqual("c1", payload.RootElement.GetProperty("client_id").GetString());

        // Internal readers (userinfo/introspection DPoP binding, ApiTokenAuthHandler) read the claim value as JSON text.
        var principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidIssuer = "https://issuer",
            ValidAudience = "api",
            IssuerSigningKey = signingKey,
            ValidTypes = new[] { "at+jwt" }
        }, out _);
        Assert.IsTrue(principal.HasClaim(c => c.Type == "cnf"));
        using var cnfDoc = JsonDocument.Parse(principal.FindFirst("cnf")!.Value);
        Assert.AreEqual("jkt-123", cnfDoc.RootElement.GetProperty("jkt").GetString());
    }
}
