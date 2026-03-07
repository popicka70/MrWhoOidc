using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.WebAuth.Middleware;

namespace MrWhoOidc.UnitTests.Middleware;

[TestClass]
public class BadRequestDiagnosticsMiddlewareTests
{
    private Mock<ILogger<BadRequestDiagnosticsMiddleware>> _loggerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<BadRequestDiagnosticsMiddleware>>();
    }

    [TestMethod]
    public async Task InvokeAsync_BadHttpRequestException_LogsWarningAndRethrows()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/test";
        context.TraceIdentifier = "test-trace-id";
        context.Connection.Id = "test-conn-id";

        var expectedException = new BadHttpRequestException("Test bad request");

        RequestDelegate next = (ctx) => throw expectedException;

        var middleware = new BadRequestDiagnosticsMiddleware(next, _loggerMock.Object);

        // Act
        var ex = await Assert.ThrowsExactlyAsync<BadHttpRequestException>(() => middleware.InvokeAsync(context));

        // Assert
        Assert.AreSame(expectedException, ex);

        // Verify logger was called with Warning and the exception
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                expectedException,
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }
}
