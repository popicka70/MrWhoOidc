using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        Assert.AreEqual(RegistrationOutcome.PendingCreated, result.Outcome);

        var dbReg = await db.Registrations.FirstOrDefaultAsync(r => r.Email == input.Email);
        Assert.IsNotNull(dbReg);
    }

    [TestMethod]
    public async Task CreateRegistrationAsync_WhenPendingRegistrationExists_ReturnsExistingPendingOutcome()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Registrations.Add(new Registration
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = "newuser@example.com",
            NormalizedEmail = "NEWUSER@EXAMPLE.COM",
            State = "pending",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var logger = new Mock<ILogger<RegistrationService>>();
        var issuerBuilder = new Mock<IIssuerBuilder>();
        var options = Options.Create(new OidcOptions());
        var provisioner = new Mock<IUserAccountProvisioner>();

        var svc = new RegistrationService(db, logger.Object, issuerBuilder.Object, options, provisioner.Object);

        var result = await svc.CreateRegistrationAsync(new RegistrationInput(
            Email: "newuser@example.com",
            FirstName: null,
            LastName: null,
            ClientId: null,
            PasswordHash: null,
            AutoApprove: false,
            IsExternalIdp: false,
            TenantCreation: null,
            TargetTenantId: tenantId));

        Assert.AreEqual(RegistrationOutcome.PendingExisting, result.Outcome);
        Assert.AreEqual("pending", result.State);
        Assert.IsTrue(result.RegistrationId.HasValue);
    }

    [TestMethod]
    public async Task CreateRegistrationAsync_WhenUserExists_ReturnsExistingUserOutcome()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Username = "existing@example.com",
            Email = "existing@example.com",
            NormalizedEmail = "EXISTING@EXAMPLE.COM"
        });
        await db.SaveChangesAsync();

        var logger = new Mock<ILogger<RegistrationService>>();
        var issuerBuilder = new Mock<IIssuerBuilder>();
        var options = Options.Create(new OidcOptions());
        var provisioner = new Mock<IUserAccountProvisioner>();

        var svc = new RegistrationService(db, logger.Object, issuerBuilder.Object, options, provisioner.Object);

        var result = await svc.CreateRegistrationAsync(new RegistrationInput(
            Email: "existing@example.com",
            FirstName: null,
            LastName: null,
            ClientId: null,
            PasswordHash: null,
            AutoApprove: false,
            IsExternalIdp: false,
            TenantCreation: null,
            TargetTenantId: tenantId));

        Assert.AreEqual(RegistrationOutcome.ExistingUser, result.Outcome);
        Assert.AreEqual("existing_user", result.State);
        Assert.IsFalse(result.RegistrationId.HasValue);
        Assert.IsTrue(result.ExistingUserId.HasValue);
        Assert.AreEqual(0, await db.Registrations.CountAsync());
    }

    [TestMethod]
    public async Task CreateRegistrationAsync_WhenAutoApproved_PersistsPasswordOnUserAccount()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var passwordHash = "argon2id$registration-hash";
        var provisioner = new UserAccountProvisioner(
            db,
            Options.Create(new UserAccountFeatureOptions { UserAccountDecouplingEnabled = true }),
            NullLogger<UserAccountProvisioner>.Instance);

        var svc = new RegistrationService(
            db,
            Mock.Of<ILogger<RegistrationService>>(),
            Mock.Of<IIssuerBuilder>(),
            Options.Create(new OidcOptions()),
            provisioner);

        var result = await svc.CreateRegistrationAsync(new RegistrationInput(
            Email: "autoreg@example.com",
            FirstName: "Auto",
            LastName: "Reg",
            ClientId: null,
            PasswordHash: passwordHash,
            AutoApprove: true,
            IsExternalIdp: false,
            TenantCreation: null,
            TargetTenantId: tenantId));

        Assert.AreEqual(RegistrationOutcome.Approved, result.Outcome);
        Assert.IsTrue(result.CreatedUserId.HasValue);

        var account = await db.UserAccounts.SingleAsync(a => a.Id == result.CreatedUserId.Value);
        Assert.AreEqual(passwordHash, account.PasswordHash);
        Assert.AreEqual("argon2id", account.HashAlgorithm);
    }
}
