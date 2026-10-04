using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.TestSupport;
using System.Net;
using System.Net.Http;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace MrWhoOidc.UnitTests;

[TestClass]
[DoNotParallelize]
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
        var validator = new ClientAssertionValidator(db);
        var (assertion, jwkJson) = SharedTestKeys.CreateClientAssertion("c1", "https://as/connect/token");
        var ok = await validator.ValidateAsync("c1", assertion, "https://as/connect/token");
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task ValidateAsync_Succeeds_WithMatchingJwk()
    {
        using var db = CreateDb();
        var (assertion, jwkJson) = SharedTestKeys.CreateClientAssertion("c1", "https://as/connect/token");
        db.Clients.Add(new ClientEntity { ClientId = "c1", PublicJwksJson = jwkJson });
        await db.SaveChangesAsync();
        var validator = new ClientAssertionValidator(db);
        var ok = await validator.ValidateAsync("c1", assertion, "https://as/connect/token");
        Assert.IsTrue(ok);
    }

    [TestMethod]
    public async Task ValidateAsync_Succeeds_WithJwksUri()
    {
        using var db = CreateDb();
        var (assertion, jwkJson) = SharedTestKeys.CreateClientAssertion("c1", "https://as/connect/token");
        var factory = new StubHttpClientFactory($"{{\"keys\":[{jwkJson}]}}");

        db.Clients.Add(new ClientEntity { ClientId = "c1", PublicJwksUri = "https://client.example/jwks" });
        await db.SaveChangesAsync();

        var validator = new ClientAssertionValidator(db, factory);
        var ok = await validator.ValidateAsync("c1", assertion, "https://as/connect/token");

        Assert.IsTrue(ok);
    }

    [TestMethod]
    public async Task ValidateAsync_Fails_WhenAssertionIsReplayed()
    {
        using var db = CreateDb();
        var clientId = $"c-{Guid.NewGuid():N}";
        var (assertion, jwkJson) = SharedTestKeys.CreateClientAssertion(clientId, "https://as/connect/token");
        db.Clients.Add(new ClientEntity { ClientId = clientId, PublicJwksJson = jwkJson });
        await db.SaveChangesAsync();
        var validator = new ClientAssertionValidator(db);

        var first = await validator.ValidateAsync(clientId, assertion, "https://as/connect/token");
        var second = await validator.ValidateAsync(clientId, assertion, "https://as/connect/token");

        Assert.IsTrue(first);
        Assert.IsFalse(second);
    }

    private static string CreateAssertion(string clientId, string audience, string algorithm, string keyId = "test-client-key")
    {
        var creds = new SigningCredentials(new JsonWebKey(SharedTestKeys.GetRsaJwkJson(keyId)), algorithm);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: clientId,
            audience: audience,
            claims: new[] { new Claim("sub", clientId), new Claim("jti", Guid.NewGuid().ToString("N")) },
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(5),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<(AuthDbContext db, string clientId)> CreateClientWithKeyAsync()
    {
        var db = CreateDb();
        var clientId = $"c-{Guid.NewGuid():N}";
        db.Clients.Add(new ClientEntity { ClientId = clientId, PublicJwksJson = SharedTestKeys.GetRsaPublicJwkJson("test-client-key") });
        await db.SaveChangesAsync();
        return (db, clientId);
    }

    [TestMethod]
    public async Task ValidateAsync_AcceptsIssuerAsAudience_WhenSupplied()
    {
        var (db, clientId) = await CreateClientWithKeyAsync();
        using var _ = db;
        var validator = new ClientAssertionValidator(db);
        var assertion = CreateAssertion(clientId, "https://as", SecurityAlgorithms.RsaSha256);

        Assert.IsTrue(await validator.ValidateAsync(clientId, assertion, ["https://as/connect/token", "https://as"]));
    }

    [TestMethod]
    public async Task ValidateAsync_RejectsIssuerAudience_WhenOnlyEndpointAllowed()
    {
        var (db, clientId) = await CreateClientWithKeyAsync();
        using var _ = db;
        var validator = new ClientAssertionValidator(db);
        var assertion = CreateAssertion(clientId, "https://evil.example", SecurityAlgorithms.RsaSha256);

        Assert.IsFalse(await validator.ValidateAsync(clientId, assertion, ["https://as/connect/token", "https://as"]));
    }

    [TestMethod]
    [DataRow(SecurityAlgorithms.RsaSsaPssSha256)]
    [DataRow(SecurityAlgorithms.RsaSsaPssSha384)]
    [DataRow(SecurityAlgorithms.RsaSsaPssSha512)]
    public async Task ValidateAsync_AcceptsRsaPssAssertions(string algorithm)
    {
        var (db, clientId) = await CreateClientWithKeyAsync();
        using var _ = db;
        var validator = new ClientAssertionValidator(db);
        var assertion = CreateAssertion(clientId, "https://as/connect/token", algorithm);

        Assert.IsTrue(await validator.ValidateAsync(clientId, assertion, "https://as/connect/token"));
    }

    [TestMethod]
    public async Task ValidateAsync_IssuerAudienceAssertion_CannotBeReplayedAtAnotherEndpoint()
    {
        var (db, clientId) = await CreateClientWithKeyAsync();
        using var _ = db;
        var validator = new ClientAssertionValidator(db);
        var assertion = CreateAssertion(clientId, "https://as", SecurityAlgorithms.RsaSha256);

        Assert.IsTrue(await validator.ValidateAsync(clientId, assertion, ["https://as/par", "https://as"]));
        Assert.IsFalse(await validator.ValidateAsync(clientId, assertion, ["https://as/connect/token", "https://as"]));
    }

    private sealed class StubHttpClientFactory(string responseBody) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler(responseBody));
    }

    private sealed class StubHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            });
    }
}
