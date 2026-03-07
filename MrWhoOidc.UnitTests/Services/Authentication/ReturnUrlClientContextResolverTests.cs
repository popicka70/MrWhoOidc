using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Services.Authentication;

[TestClass]
public class ReturnUrlClientContextResolverTests
{
    private Mock<IAuthorizeRequestResolver> _resolverMock = null!;
    private Mock<IClientStore> _clientStoreMock = null!;
    private ReturnUrlClientContextResolver _sut = null!;

    [TestInitialize]
    public void Setup()
    {
        _resolverMock = new Mock<IAuthorizeRequestResolver>();
        _clientStoreMock = new Mock<IClientStore>();
        _sut = new ReturnUrlClientContextResolver(
            _resolverMock.Object,
            _clientStoreMock.Object,
            NullLogger<ReturnUrlClientContextResolver>.Instance);
    }

    private HttpContext CreateHttpContext()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("localhost");

        // Setup RequestServices to avoid NullReferenceException in GetIssuer extension
        var services = new ServiceCollection();
        services.AddSingleton(new OidcOptions { Issuer = "https://localhost" });
        httpContext.RequestServices = services.BuildServiceProvider();

        return httpContext;
    }

    [TestMethod]
    public async Task TryResolveClientAsync_WithValidAuthorizeUrl_ResolvesClient()
    {
        // Arrange
        var httpContext = CreateHttpContext();
        var returnUrl = "/authorize?client_id=client123";
        var expectedClient = new ClientEntity { ClientId = "client123" };

        _resolverMock.Setup(r => r.ResolveAsync(
            It.IsAny<IEnumerable<KeyValuePair<string, string>>>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizeRequestResolution(
                new AuthorizeRequest("code", "client123", null, null, null, null, null, null, null, null),
                "client123",
                "bucket",
                "query",
                true,
                null,
                null,
                0,
                null));

        _clientStoreMock.Setup(c => c.FindByClientIdAsync("client123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedClient);

        // Act
        var result = await _sut.TryResolveClientAsync(httpContext, returnUrl);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("client123", result.ClientId);
    }

    [TestMethod]
    public async Task TryResolveClientAsync_WithMalformedUrlThatThrowsInTryParse_ReturnsNullSafely()
    {
        // Arrange
        var httpContext = CreateHttpContext();

        // This will pass LooksLikeLocalUrl (starts with "/")
        // but will fail in TryParseAuthorizeReturnUrl when creating new Uri("http://local" + returnUrl)
        // due to UriFormatException, hitting the catch block.
        var excessivelyLongPath = "/" + new string('a', 70000);

        // Act
        var result = await _sut.TryResolveClientAsync(httpContext, excessivelyLongPath);

        // Assert
        Assert.IsNull(result, "Expected null because the parsing should fail and catch the exception safely.");

        // Ensure resolver was never called because it failed to parse
        _resolverMock.Verify(r => r.ResolveAsync(
            It.IsAny<IEnumerable<KeyValuePair<string, string>>>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
