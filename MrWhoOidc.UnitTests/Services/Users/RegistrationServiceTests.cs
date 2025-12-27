using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Users;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Services.Users;

[TestClass]
public sealed class RegistrationServiceTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public async Task CreateRegistrationAsync_CreatesRegistrationRecord()
    {
        // Arrange
        using var db = CreateDb();
        var logger = new Mock<ILogger<RegistrationService>>();
        var issuerBuilder = new Mock<IIssuerBuilder>();
        issuerBuilder.Setup(x => x.BuildIssuer(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string baseUri, string slug) => $"{baseUri}/{slug}");
        var options = Options.Create(new OidcOptions());
        var provisioner = new Mock<IUserAccountProvisioner>();

        var svc = new RegistrationService(db, logger.Object, issuerBuilder.Object, options, provisioner.Object);
        var input = new RegistrationInput(
            Email: "newuser@example.com",
            FirstName: "New",
            LastName: "User",
            ClientId: null,
            PasswordHash: "hashed",
            AutoApprove: false,
            TenantCreation: new TenantCreationInput("new-tenant", "New Tenant", null)
        );

        // Act
        var result = await svc.CreateRegistrationAsync(input);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("pending", result.State);

        var dbReg = await db.Registrations.FirstOrDefaultAsync(r => r.Email == input.Email);
        Assert.IsNotNull(dbReg);
    }
}
