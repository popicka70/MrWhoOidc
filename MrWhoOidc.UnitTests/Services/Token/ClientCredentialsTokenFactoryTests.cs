using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Services.Token;

[TestClass]
public sealed class ClientCredentialsTokenFactoryTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    private static IOptions<AuthOptions> Options()
        => Microsoft.Extensions.Options.Options.Create(new AuthOptions());

    [TestMethod]
    public async Task CreateTokenAsync_Succeeds()
    {
        using var db = CreateDb();
        var client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<System.Security.Claims.Claim>>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("fake-jwt");

        var settingsSvc = new MockTenantSettingsService();
        var scopeResolver = new MockScopeResolver();
        var logger = new Mock<ILogger<ClientCredentialsTokenFactory>>();

        var factory = new ClientCredentialsTokenFactory(
            db, jwtSvc.Object, Options(), settingsSvc, scopeResolver, logger.Object);

        var request = new ClientCredentialsRequest("c1", "api", new[] { "openid" }, "https://issuer");
        var (ok, payload, error, status) = await factory.CreateTokenAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        var anon = (dynamic)payload!;
        Assert.AreEqual("fake-jwt", (string)anon.access_token);
    }
}
