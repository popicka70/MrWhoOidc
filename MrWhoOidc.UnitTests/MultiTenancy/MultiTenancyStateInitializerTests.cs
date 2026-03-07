using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.UnitTests.MultiTenancy;

[TestClass]
public sealed class MultiTenancyStateInitializerTests
{
    [TestMethod]
    public async Task StartAsync_WhenLicenseServiceThrowsException_LogsErrorAndDoesNotThrow()
    {
        // Arrange
        var mockLicenseService = new Mock<ILicenseService>();
        var expectedException = new Exception("Test exception");
        mockLicenseService
            .Setup(x => x.GetCurrentLicenseAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        var services = new ServiceCollection();
        services.AddScoped<ILicenseService>(_ => mockLicenseService.Object);
        var serviceProvider = services.BuildServiceProvider();

        var mockStateProvider = new Mock<IMultiTenancyStateProvider>();
        var mockLogger = new Mock<ILogger<MultiTenancyStateInitializer>>();

        var initializer = new MultiTenancyStateInitializer(
            serviceProvider,
            mockStateProvider.Object,
            mockLogger.Object);

        // Act
        // The test succeeds if this does not throw an exception
        await initializer.StartAsync(CancellationToken.None);

        // Assert
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to initialize multi-tenancy state from license.")),
                expectedException,
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }
}
