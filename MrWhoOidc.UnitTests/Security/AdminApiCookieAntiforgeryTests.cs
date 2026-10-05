using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;
using MrWhoOidc.WebAuth.Security.ApiBearer;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public sealed class AdminApiCookieAntiforgeryTests
{
    [TestMethod]
    public async Task CookieWrite_WithoutValidAntiforgeryToken_IsRejected()
    {
        var antiforgery = new Mock<IAntiforgery>();
        antiforgery.Setup(service => service.IsRequestValidAsync(It.IsAny<HttpContext>())).ReturnsAsync(false);
        var httpContext = CreateContext(antiforgery.Object, AuthenticateResult.NoResult());

        var result = await AdminApiAntiforgeryExtensions.ValidateCookieMutationAsync(httpContext);

        Assert.IsNotNull(result);
        await result.ExecuteAsync(httpContext);
        Assert.AreEqual(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        antiforgery.Verify(service => service.IsRequestValidAsync(httpContext), Times.Once);
    }

    [TestMethod]
    public async Task CookieWrite_WithValidAntiforgeryToken_IsAllowed()
    {
        var antiforgery = new Mock<IAntiforgery>();
        antiforgery.Setup(service => service.IsRequestValidAsync(It.IsAny<HttpContext>())).ReturnsAsync(true);
        var httpContext = CreateContext(antiforgery.Object, AuthenticateResult.NoResult());

        var result = await AdminApiAntiforgeryExtensions.ValidateCookieMutationAsync(httpContext);

        Assert.IsNull(result);
        antiforgery.Verify(service => service.IsRequestValidAsync(httpContext), Times.Once);
    }

    [TestMethod]
    public async Task BearerWrite_BypassesAntiforgeryForAuthenticatedApiClient()
    {
        var antiforgery = new Mock<IAntiforgery>(MockBehavior.Strict);
        var bearerPrincipal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "client")], ApiTokenAuthHandler.SchemeName));
        var bearerTicket = new AuthenticationTicket(bearerPrincipal, ApiTokenAuthHandler.SchemeName);
        var httpContext = CreateContext(antiforgery.Object, AuthenticateResult.Success(bearerTicket));

        var result = await AdminApiAntiforgeryExtensions.ValidateCookieMutationAsync(httpContext);

        Assert.IsNull(result);
        antiforgery.VerifyNoOtherCalls();
    }

    private static DefaultHttpContext CreateContext(IAntiforgery antiforgery, AuthenticateResult bearerResult)
    {
        var authentication = new Mock<IAuthenticationService>();
        authentication.Setup(service => service.AuthenticateAsync(It.IsAny<HttpContext>(), ApiTokenAuthHandler.SchemeName))
            .ReturnsAsync(bearerResult);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(antiforgery)
            .AddSingleton(authentication.Object)
            .BuildServiceProvider();

        return new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
    }
}
