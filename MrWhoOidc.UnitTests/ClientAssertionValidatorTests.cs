using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.TestSupport;
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
}
