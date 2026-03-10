using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        var validator = new ClientAssertionValidator(db, new ConfigurationBuilder().Build());
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
        var validator = new ClientAssertionValidator(db, new ConfigurationBuilder().Build());
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

        var validator = new ClientAssertionValidator(db, new ConfigurationBuilder().Build(), factory);
        var ok = await validator.ValidateAsync("c1", assertion, "https://as/connect/token");

        Assert.IsTrue(ok);
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
