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
            .ReturnsAsync(new RegistrationResult(registration.Id, "approved", userId));

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
}
