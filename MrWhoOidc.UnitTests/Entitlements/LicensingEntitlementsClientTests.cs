using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Entitlements.Contracts;
using MrWhoOidc.Auth.Entitlements.Options;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Entitlements;

[TestClass]
public class LicensingEntitlementsClientTests
{
    private sealed class TestHttpMessageHandler(HttpResponseMessage responseMessage) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responseMessage);
        }
    }

    [TestMethod]
    public async Task GetSignedLicenseTokenAsync_WhenResponseIsErrorAndInvalidJson_IgnoresJsonExceptionAndReturnsGenericError()
    {
        // Arrange
        var options = new LicensingIntegrationOptions
        {
            Enabled = true,
            BaseUrl = "https://example.com",
            Audience = "test-audience"
        };
        var optionsMock = new Mock<IOptions<LicensingIntegrationOptions>>();
        optionsMock.Setup(o => o.Value).Returns(options);

        var jwtServiceMock = new Mock<IJwtService>();
        jwtServiceMock
            .Setup(j => j.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<System.Security.Claims.Claim>>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>()))
            .ReturnsAsync("dummy-token");

        var responseMessage = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("This is not valid JSON")
        };

        var handler = new TestHttpMessageHandler(responseMessage);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        };

        var logger = NullLogger<LicensingEntitlementsClient>.Instance;

        var client = new LicensingEntitlementsClient(httpClient, optionsMock.Object, jwtServiceMock.Object, logger);
        var request = new SignedLicenseTokenRequest
        {
            SubjectId = "sub123",
            ProductKey = "prod123"
        };

        // Act
        var result = await client.GetSignedLicenseTokenAsync(request, "https://issuer.com");

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual("service_error", result.Error.Error);
        Assert.AreEqual("LicensingService returned status 400", result.Error.ErrorDescription);
    }
}
