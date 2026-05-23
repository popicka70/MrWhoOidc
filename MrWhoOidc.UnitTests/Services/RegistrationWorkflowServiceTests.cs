using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.Users;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Services;

[TestClass]
public sealed class RegistrationWorkflowServiceTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public async Task ApproveRegistrationAsync_WhenEmailFails_LogsWarning()
    {
        // Arrange
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "test@example.com",
            TenantId = Guid.NewGuid()
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var registration = new Registration
        {
            Id = Guid.NewGuid(),
            Email = "test@example.com",
            State = "pending",
            ClientId = null
        };

        var loggerMock = new Mock<ILogger<RegistrationWorkflowService>>();
        var emailWorkflowMock = new Mock<IEmailConfirmationWorkflow>();
        var tenantAccessorMock = new Mock<ITenantAccessor>();
        var auditMock = new Mock<IAuditSink>();
        var domainServiceMock = new Mock<IRegistrationService>();

        domainServiceMock.Setup(x => x.ApproveRegistrationAsync(registration.Id, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistrationResult(registration.Id, "approved", RegistrationOutcome.Approved, userId));

        var exceptionToThrow = new InvalidOperationException("Email service down");
        emailWorkflowMock.Setup(x => x.SendPrimaryAsync(It.Is<User>(u => u.Id == userId), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exceptionToThrow);

        var svc = new RegistrationWorkflowService(
            db,
            loggerMock.Object,
            emailWorkflowMock.Object,
            tenantAccessorMock.Object,
            auditMock.Object,
            domainServiceMock.Object);

        // Act
        var resultUserId = await svc.ApproveRegistrationAsync(registration);

        // Assert
        Assert.AreEqual(userId, resultUserId);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to dispatch confirmation email")),
                exceptionToThrow,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TestMethod]
    public async Task CreateAndMaybeApproveRegistrationAsync_WhenPendingAlreadyExists_ReturnsOutcomeWithoutSendingEmail()
    {
        using var db = CreateDb();

        var loggerMock = new Mock<ILogger<RegistrationWorkflowService>>();
        var emailWorkflowMock = new Mock<IEmailConfirmationWorkflow>();
        var tenantAccessorMock = new Mock<ITenantAccessor>();
        tenantAccessorMock.SetupGet(x => x.CurrentTenant).Returns(new TenantContext
        {
            TenantId = Guid.NewGuid(),
            Slug = "default",
            Name = "Default",
            IssuerUri = "https://localhost:8443/t/default"
        });
        var auditMock = new Mock<IAuditSink>();
        var domainServiceMock = new Mock<IRegistrationService>();
        var pendingRegistrationId = Guid.NewGuid();

        domainServiceMock.Setup(x => x.CreateRegistrationAsync(It.IsAny<RegistrationInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistrationResult(pendingRegistrationId, "pending", RegistrationOutcome.PendingExisting));

        var svc = new RegistrationWorkflowService(
            db,
            loggerMock.Object,
            emailWorkflowMock.Object,
            tenantAccessorMock.Object,
            auditMock.Object,
            domainServiceMock.Object);

        var result = await svc.CreateAndMaybeApproveRegistrationAsync(
            "test@example.com",
            null,
            null,
            null,
            null,
            isExternalIdp: false,
            autoApprove: false);

        Assert.AreEqual(RegistrationOutcome.PendingExisting, result.Outcome);
        Assert.AreEqual(pendingRegistrationId, result.RegistrationId);
        emailWorkflowMock.Verify(x => x.SendPrimaryAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task CreateAndMaybeApproveRegistrationAsync_WithTargetTenantId_PassesExplicitTenantToDomainService()
    {
        using var db = CreateDb();

        var loggerMock = new Mock<ILogger<RegistrationWorkflowService>>();
        var emailWorkflowMock = new Mock<IEmailConfirmationWorkflow>();
        var ambientTenantId = Guid.NewGuid();
        var targetTenantId = Guid.NewGuid();
        var tenantAccessorMock = new Mock<ITenantAccessor>();
        tenantAccessorMock.SetupGet(x => x.CurrentTenant).Returns(new TenantContext
        {
            TenantId = ambientTenantId,
            Slug = "default",
            Name = "Default",
            IssuerUri = "https://localhost:8443/t/default"
        });
        var auditMock = new Mock<IAuditSink>();
        var domainServiceMock = new Mock<IRegistrationService>();
        RegistrationInput? capturedInput = null;

        domainServiceMock.Setup(x => x.CreateRegistrationAsync(It.IsAny<RegistrationInput>(), It.IsAny<CancellationToken>()))
            .Callback<RegistrationInput, CancellationToken>((input, _) => capturedInput = input)
            .ReturnsAsync(new RegistrationResult(Guid.NewGuid(), "pending", RegistrationOutcome.PendingCreated));

        var svc = new RegistrationWorkflowService(
            db,
            loggerMock.Object,
            emailWorkflowMock.Object,
            tenantAccessorMock.Object,
            auditMock.Object,
            domainServiceMock.Object);

        await svc.CreateAndMaybeApproveRegistrationAsync(
            "invitee@example.com",
            "Invite",
            "User",
            null,
            "password-hash",
            isExternalIdp: false,
            autoApprove: true,
            targetTenantId: targetTenantId);

        Assert.IsNotNull(capturedInput);
        Assert.AreEqual(targetTenantId, capturedInput.TargetTenantId);
        Assert.AreNotEqual(ambientTenantId, capturedInput.TargetTenantId);
    }
}
