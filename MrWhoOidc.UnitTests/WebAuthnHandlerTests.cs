using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class WebAuthnHandlerTests
{
    [TestMethod]
    public async Task RenameCredentialAsync_ReturnsOk_WhenServiceUpdates()
    {
        await using var db = CreateDbContext();
        var credentialId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var webAuthnService = new Mock<IWebAuthnService>();
        webAuthnService
            .Setup(s => s.UpdateCredentialNameAsync(userId, credentialId, "Renamed Key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handler = CreateHandler(webAuthnService.Object, db);
        var context = CreateAuthenticatedJsonContext(userId, credentialId, "{\"friendlyName\":\"Renamed Key\"}");

        var result = await handler.RenameCredentialAsync(context);
        var (statusCode, _) = await ExecuteResultAsync(result, context);

        Assert.AreEqual(StatusCodes.Status200OK, statusCode);
        webAuthnService.VerifyAll();
    }

    [TestMethod]
    public async Task RemoveCredentialAsync_ReturnsNotFound_WhenServiceReturnsFalse()
    {
        await using var db = CreateDbContext();
        var credentialId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var webAuthnService = new Mock<IWebAuthnService>();
        webAuthnService
            .Setup(s => s.RemoveCredentialAsync(userId, credentialId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var handler = CreateHandler(webAuthnService.Object, db);
        var context = CreateAuthenticatedJsonContext(userId, credentialId, "{}");

        var result = await handler.RemoveCredentialAsync(context);
        var (statusCode, _) = await ExecuteResultAsync(result, context);

        Assert.AreEqual(StatusCodes.Status404NotFound, statusCode);
        webAuthnService.VerifyAll();
    }

    private static WebAuthnHandler CreateHandler(IWebAuthnService webAuthnService, AuthDbContext db)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        var multiTenancy = new Mock<IMultiTenancyOptions>();
        var settings = new Mock<ITenantSettingsService>();

        return new WebAuthnHandler(
            webAuthnService,
            tenantAccessor.Object,
            db,
            NullLogger<WebAuthnHandler>.Instance,
            multiTenancy.Object,
            settings.Object);
    }

    private static DefaultHttpContext CreateAuthenticatedJsonContext(Guid userId, Guid credentialId, string jsonBody)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
            "test"));

        context.Request.RouteValues["credentialId"] = credentialId.ToString();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody));
        context.Response.Body = new MemoryStream();

        return context;
    }

    private static async Task<(int statusCode, string body)> ExecuteResultAsync(IResult result, DefaultHttpContext context)
    {
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;

        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static AuthDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase("webauthn-handler-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new AuthDbContext(options);
    }
}
