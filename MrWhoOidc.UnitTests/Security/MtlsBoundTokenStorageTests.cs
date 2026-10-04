using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.Security;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers.Introspection;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// 2026-10-04 assessment: the mTLS certificate thumbprint (x5t#S256) was stored in CnfJkt and therefore reported as
/// cnf.jkt (a JWK thumbprint) by introspection, and a DPoP proof was demanded for a certificate-bound token.
/// </summary>
[TestClass]
public sealed class MtlsBoundTokenStorageTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [TestMethod]
    public async Task ClientCredentials_Stores_The_Certificate_Thumbprint_As_X5tS256_Not_Jkt()
    {
        using var db = CreateDb();
        db.Clients.Add(new ClientEntity { ClientId = "m2m", AllowClientCredentials = true });
        await db.SaveChangesAsync();

        var jwt = new Mock<IJwtService>();
        jwt.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<System.Security.Claims.Claim>>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("cc-jwt");
        var factory = new ClientCredentialsTokenFactory(db, jwt.Object, Options.Create(new AuthOptions()), new MockTenantSettingsService(),
            new MockScopeResolver(), new TokenLifetimeResolver(), NullLogger<ClientCredentialsTokenFactory>.Instance);

        var (ok, _, _, _) = await factory.CreateTokenAsync(new ClientCredentialsRequest("m2m", "api", Array.Empty<string>(), "https://issuer", null, "cert-thumb"));

        Assert.IsTrue(ok);
        var row = await db.Tokens.SingleAsync(t => t.TokenHash == CryptoHelper.ComputeSha256Base64("cc-jwt"));
        Assert.AreEqual("cert-thumb", row.CnfX5tS256);
        Assert.IsNull(row.CnfJkt, "a certificate thumbprint is not a DPoP key thumbprint");
    }

    [TestMethod]
    public async Task Introspection_Reports_X5tS256_For_A_Certificate_Bound_Token_Without_Demanding_DPoP()
    {
        using var db = CreateDb();
        db.Tokens.Add(new Token
        {
            TenantId = new Guid("00000000-0000-0000-0000-000000000001"),
            Type = "access",
            TokenHash = CryptoHelper.ComputeSha256Base64("opaque-mtls"),
            ClientId = "m2m",
            Audience = "api",
            Jti = "j1",
            ScopesJson = "[]",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CnfX5tS256 = "cert-thumb"
        });
        await db.SaveChangesAsync();

        var (response, error) = await IntrospectAsync(db, "opaque-mtls");

        Assert.IsNull(error, "no DPoP proof may be required for an mTLS-bound token");
        Assert.IsNotNull(response);
        Assert.AreEqual(true, response["active"]);
        var cnf = JsonSerializer.SerializeToElement(response["cnf"]);
        Assert.AreEqual("cert-thumb", cnf.GetProperty("x5t#S256").GetString());
        Assert.IsFalse(cnf.TryGetProperty("jkt", out _));
    }

    private static async Task<(Dictionary<string, object?>? Response, IResult? Error)> IntrospectAsync(AuthDbContext db, string token)
    {
        var options = Options.Create(new AuthOptions { IntrospectionDefaultResponseFields = ["active", "cnf", "jti"] });
        var dpop = new Mock<IDPoPValidator>(MockBehavior.Strict); // must not be asked for a proof
        var introspector = new OpaqueTokenIntrospector(
            db,
            new MrWhoOidc.WebAuth.Handlers.Introspection.DPoPValidator(dpop.Object, Mock.Of<IDPoPReplayCache>(), Mock.Of<IDPoPNonceStore>()),
            new AudiencePolicy(options),
            new ResponseShaper(options),
            NullLogger<OpaqueTokenIntrospector>.Instance);

        return await introspector.IntrospectAsync(new IntrospectionContext
        {
            Request = new IntrospectionRequest(token, null, "m2m", null, null, null),
            Client = new ClientEntity { ClientId = "m2m" },
            Issuer = "https://issuer",
            Endpoint = "https://issuer/introspect",
            HttpContext = new DefaultHttpContext(),
            ClientBucket = "b",
            MetricTags = []
        });
    }
}
