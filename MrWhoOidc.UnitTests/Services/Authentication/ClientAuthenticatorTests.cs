using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.Authentication;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;
using System.Threading;
using System.Text;
using System.Threading.Tasks;

namespace MrWhoOidc.UnitTests.Services.Authentication;

[TestClass]
public class ClientAuthenticatorTests
{
    private Mock<IClientAuthenticationService> _authServiceMock = null!;
    private Mock<IMtlsThumbprintResolver> _mtlsResolverMock = null!;
    private ClientAuthenticator _authenticator = null!;

    [TestInitialize]
    public void Initialize()
    {
        _authServiceMock = new Mock<IClientAuthenticationService>();
        _mtlsResolverMock = new Mock<IMtlsThumbprintResolver>();
        _authenticator = new ClientAuthenticator(_authServiceMock.Object, _mtlsResolverMock.Object, NullLogger<ClientAuthenticator>.Instance);
    }

    [TestMethod]
    public async Task AuthenticateAsync_InvalidBasicAuthBase64_CatchesExceptionAndReturnsMissingClientId()
    {
        // Arrange
        var context = new DefaultHttpContext();
        // Provide an invalid base64 string
        context.Request.Headers.Authorization = "Basic !@#$%^&*()";

        var authContext = new ClientAuthenticationContext { Usage = ClientAuthenticationUsage.TokenEndpoint };

        // Act
        var result = await _authenticator.AuthenticateAsync(context, authContext);

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ClientAuthenticationMethod.None, result.Method);
        Assert.IsNotNull(result.ErrorResult);
        // The error result is an IResult from Results.BadRequest
    }

    [TestMethod]
    public async Task AuthenticateAsync_InvalidBasicAuthFormat_CatchesExceptionAndReturnsMissingClientId()
    {
        // Arrange
        var context = new DefaultHttpContext();
        // Provide a valid base64 string but missing the colon
        context.Request.Headers.Authorization = "Basic " + System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("invalid-format"));

        var authContext = new ClientAuthenticationContext { Usage = ClientAuthenticationUsage.TokenEndpoint };

        // Act
        var result = await _authenticator.AuthenticateAsync(context, authContext);

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ClientAuthenticationMethod.None, result.Method);
        Assert.IsNotNull(result.ErrorResult);
    }

    [TestMethod]
    public async Task AuthenticateAsync_BasicAuth_DecodesFormUrlEncodedClientCredentials()
    {
        var context = new DefaultHttpContext();
        var clientId = "client+id";
        var clientSecret = "secret/+value";
        // Per RFC 7617, HTTP Basic credentials are sent verbatim after base64
        // decoding — they must NOT be URL-encoded. Base64 secrets legitimately
        // contain '+' which WebUtility.UrlDecode would corrupt into a space.
        var encodedPair = $"{clientId}:{clientSecret}";
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(encodedPair));

        ClientCredentialInput? capturedInput = null;
        _authServiceMock
            .Setup(x => x.AuthenticateAsync(It.IsAny<ClientCredentialInput>(), It.IsAny<CancellationToken>()))
            .Callback<ClientCredentialInput, CancellationToken>((input, _) => capturedInput = input)
            .ReturnsAsync(new ClientAuthResult(true, new MrWhoOidc.Auth.Persistence.Client { ClientId = clientId }));

        var authContext = new ClientAuthenticationContext
        {
            Usage = ClientAuthenticationUsage.TokenEndpoint,
            GrantType = "authorization_code"
        };

        var result = await _authenticator.AuthenticateAsync(context, authContext);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(ClientAuthenticationMethod.ClientSecretBasic, result.Method);
        Assert.IsNotNull(capturedInput);
        Assert.AreEqual(clientId, capturedInput.ClientId);
        Assert.AreEqual(clientSecret, capturedInput.ClientSecret);
    }
}
