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
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
        // RFC 6749 §2.3.1: the client credentials are form-urlencoded before
        // being base64-encoded into the Basic header. '+' and '/' in a base64
        // secret therefore arrive as %2B and %2F.
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client%2Bid:secret%2F%2Bvalue"));

        ClientCredentialInput? capturedInput = null;
        _authServiceMock
            .Setup(x => x.AuthenticateAsync(It.IsAny<ClientCredentialInput>(), It.IsAny<CancellationToken>()))
            .Callback<ClientCredentialInput, CancellationToken>((input, _) => capturedInput = input)
            .ReturnsAsync(new ClientAuthResult(true, new MrWhoOidc.Auth.Persistence.Client { ClientId = "client+id" }));

        var authContext = new ClientAuthenticationContext
        {
            Usage = ClientAuthenticationUsage.TokenEndpoint,
            GrantType = "authorization_code"
        };

        var result = await _authenticator.AuthenticateAsync(context, authContext);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(ClientAuthenticationMethod.ClientSecretBasic, result.Method);
        Assert.IsNotNull(capturedInput);
        Assert.AreEqual("client+id", capturedInput.ClientId);
        Assert.AreEqual("secret/+value", capturedInput.ClientSecret);
    }

    [TestMethod]
    public async Task AuthenticateAsync_BasicAuth_PreservesVerbatimPlusCharacters()
    {
        var context = new DefaultHttpContext();
        // Some clients send base64 secrets verbatim instead of form-urlencoding
        // them. A literal '+' must stay a plus, not become a space.
        var clientId = "client+id";
        var clientSecret = "secret/+value";
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

    private static DefaultHttpContext FormContext(Dictionary<string, string> form, string? basic = null)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(form.ToDictionary(kv => kv.Key, kv => new Microsoft.Extensions.Primitives.StringValues(kv.Value)));
        if (basic is not null)
        {
            context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(basic));
        }
        return context;
    }

    private void ServiceAccepts(MrWhoOidc.Auth.Persistence.Client client) =>
        _authServiceMock
            .Setup(x => x.AuthenticateAsync(It.IsAny<ClientCredentialInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientAuthResult(true, client));

    private static readonly ClientAuthenticationContext TokenCtx = new() { Usage = ClientAuthenticationUsage.TokenEndpoint, GrantType = "authorization_code" };

    [TestMethod]
    public async Task AuthenticateAsync_BasicAndFormSecret_RejectedAsMultipleMethods()
    {
        ServiceAccepts(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1" });
        var http = FormContext(new() { ["client_secret"] = "s" }, basic: "c1:s");

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Basic realm=\"token\", charset=\"UTF-8\"", http.Response.Headers.WWWAuthenticate.ToString());
        _authServiceMock.Verify(x => x.AuthenticateAsync(It.IsAny<ClientCredentialInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task AuthenticateAsync_SecretAndAssertion_RejectedAsMultipleMethods()
    {
        ServiceAccepts(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1" });
        var http = FormContext(new()
        {
            ["client_id"] = "c1",
            ["client_secret"] = "s",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = "a.b.c",
        });

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task AuthenticateAsync_BasicClientIdDiffersFromForm_Rejected()
    {
        ServiceAccepts(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1" });
        var http = FormContext(new() { ["client_id"] = "other" }, basic: "c1:s");

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task AuthenticateAsync_SecretUsedByPrivateKeyJwtClient_Rejected()
    {
        ServiceAccepts(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", TokenEndpointAuthMethod = "private_key_jwt" });
        var http = FormContext(new() { ["client_id"] = "c1", ["client_secret"] = "s" });

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsFalse(result.IsSuccess, "registered token_endpoint_auth_method must be enforced");
    }

    [TestMethod]
    public async Task AuthenticateAsync_FormCredentialFailure_Returns401InvalidClientWithoutBasicChallenge()
    {
        _authServiceMock
            .Setup(x => x.AuthenticateAsync(It.IsAny<ClientCredentialInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientAuthResult(false, null, "invalid_client"));
        var http = FormContext(new() { ["client_id"] = "c1", ["client_secret"] = "wrong" });

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(401, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result.ErrorResult!).StatusCode);
        var payload = (Dictionary<string, object?>)((Microsoft.AspNetCore.Http.IValueHttpResult)result.ErrorResult!).Value!;
        Assert.AreEqual("invalid_client", payload["error"]);
        Assert.AreEqual(string.Empty, http.Response.Headers.WWWAuthenticate.ToString());
    }

    [TestMethod]
    public async Task AuthenticateAsync_Mtls_ReturnsVerifiedCertificateThumbprint()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=client-auth-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _mtlsResolverMock.Setup(x => x.ResolveThumbprint(certificate)).Returns("cert-thumb-123");
        ServiceAccepts(new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "mtls-client",
            TokenEndpointAuthMethod = "self_signed_tls_client_auth"
        });

        var http = FormContext(new() { ["client_id"] = "mtls-client" });
        http.Connection.ClientCertificate = certificate;

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(ClientAuthenticationMethod.Mtls, result.Method);
        Assert.AreEqual("cert-thumb-123", result.MtlsX5tS256);
    }

    [TestMethod]
    [DataRow("none", ClientAuthenticationMethod.None, true)]
    [DataRow("none", ClientAuthenticationMethod.ClientSecretPost, false)]
    [DataRow("client_secret_basic", ClientAuthenticationMethod.ClientSecretBasic, true)]
    [DataRow("client_secret_basic", ClientAuthenticationMethod.ClientSecretPost, false)]
    [DataRow("private_key_jwt", ClientAuthenticationMethod.PrivateKeyJwt, true)]
    [DataRow("private_key_jwt", ClientAuthenticationMethod.None, false)]
    [DataRow("self_signed_tls_client_auth", ClientAuthenticationMethod.Mtls, true)]
    [DataRow("unknown_method", ClientAuthenticationMethod.ClientSecretPost, false)]
    public void IsMethodAllowed_EnforcesRegisteredMethod(string registered, ClientAuthenticationMethod used, bool expected)
    {
        var client = new MrWhoOidc.Auth.Persistence.Client { TokenEndpointAuthMethod = registered };
        Assert.AreEqual(expected, ClientAuthenticator.IsMethodAllowed(client, used));
    }

    [TestMethod]
    public void IsMethodAllowed_UnregisteredClient_HonoursToggles()
    {
        var client = new MrWhoOidc.Auth.Persistence.Client { AllowClientSecretPost = false };
        Assert.IsFalse(ClientAuthenticator.IsMethodAllowed(client, ClientAuthenticationMethod.ClientSecretPost));
        Assert.IsTrue(ClientAuthenticator.IsMethodAllowed(client, ClientAuthenticationMethod.ClientSecretBasic));
    }

    // RFC 7523 §2.2: a client authenticating with a JWT client assertion is identified by the
    // assertion's iss/sub claims and may omit the RFC 6749 client_id parameter entirely.

    [TestMethod]
    public async Task AuthenticateAsync_ClientAssertionWithoutClientId_DerivesClientFromIss()
    {
        var client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "dyn_client_1", TokenEndpointAuthMethod = "private_key_jwt" };
        ClientCredentialInput? captured = null;
        _authServiceMock
            .Setup(x => x.AuthenticateAsync(It.IsAny<ClientCredentialInput>(), It.IsAny<CancellationToken>()))
            .Callback<ClientCredentialInput, CancellationToken>((input, _) => captured = input)
            .ReturnsAsync(new ClientAuthResult(true, client));

        var http = FormContext(new()
        {
            ["grant_type"] = "authorization_code",
            ["client_assertion"] = FakeJwt(iss: "dyn_client_1", sub: "dyn_client_1"),
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
        });

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(ClientAuthenticationMethod.PrivateKeyJwt, result.Method);
        Assert.IsNotNull(captured);
        Assert.AreEqual("dyn_client_1", captured.ClientId);
    }

    [TestMethod]
    public async Task AuthenticateAsync_ClientAssertionWithoutClientId_FallsBackToSub()
    {
        var client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "dyn_client_2", TokenEndpointAuthMethod = "private_key_jwt" };
        ClientCredentialInput? captured = null;
        _authServiceMock
            .Setup(x => x.AuthenticateAsync(It.IsAny<ClientCredentialInput>(), It.IsAny<CancellationToken>()))
            .Callback<ClientCredentialInput, CancellationToken>((input, _) => captured = input)
            .ReturnsAsync(new ClientAuthResult(true, client));

        var http = FormContext(new()
        {
            ["client_assertion"] = FakeJwt(iss: null, sub: "dyn_client_2"),
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
        });

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(captured);
        Assert.AreEqual("dyn_client_2", captured.ClientId);
    }

    [TestMethod]
    public async Task AuthenticateAsync_ClientAssertionWithoutIssOrSub_ReturnsMissingClientId()
    {
        var http = FormContext(new()
        {
            ["client_assertion"] = FakeJwt(iss: null, sub: null),
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
        });

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsFalse(result.IsSuccess);
        _authServiceMock.Verify(x => x.AuthenticateAsync(It.IsAny<ClientCredentialInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task AuthenticateAsync_MalformedClientAssertionWithoutClientId_ReturnsMissingClientId()
    {
        var http = FormContext(new()
        {
            ["client_assertion"] = "not.a.jwt",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
        });

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsFalse(result.IsSuccess);
        _authServiceMock.Verify(x => x.AuthenticateAsync(It.IsAny<ClientCredentialInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task AuthenticateAsync_ClientAssertionWithForeignAssertionType_ReturnsMissingClientId()
    {
        // client_assertion without the RFC 7523 jwt-bearer assertion type is not a JWT client
        // authentication, so the iss claim must not be used to identify the client.
        var http = FormContext(new()
        {
            ["client_assertion"] = FakeJwt(iss: "dyn_client_3", sub: "dyn_client_3"),
            ["client_assertion_type"] = "urn:example:other-assertion"
        });

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsFalse(result.IsSuccess);
        _authServiceMock.Verify(x => x.AuthenticateAsync(It.IsAny<ClientCredentialInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task AuthenticateAsync_ClientIdAndClientAssertion_IssMismatchStillAuthenticatesThroughService()
    {
        // An explicit client_id wins; the (mocked) assertion validator is responsible for rejecting
        // an assertion whose iss does not equal the presented client_id.
        var client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "explicit-client", TokenEndpointAuthMethod = "private_key_jwt" };
        ClientCredentialInput? captured = null;
        _authServiceMock
            .Setup(x => x.AuthenticateAsync(It.IsAny<ClientCredentialInput>(), It.IsAny<CancellationToken>()))
            .Callback<ClientCredentialInput, CancellationToken>((input, _) => captured = input)
            .ReturnsAsync(new ClientAuthResult(true, client));

        var http = FormContext(new()
        {
            ["client_id"] = "explicit-client",
            ["client_assertion"] = FakeJwt(iss: "someone-else", sub: "someone-else"),
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
        });

        var result = await _authenticator.AuthenticateAsync(http, TokenCtx);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(captured);
        Assert.AreEqual("explicit-client", captured.ClientId);
    }

    [TestMethod]
    public void TryReadClientIdFromAssertion_HandlesInvalidInput()
    {
        Assert.IsNull(ClientAuthenticator.TryReadClientIdFromAssertion(null));
        Assert.IsNull(ClientAuthenticator.TryReadClientIdFromAssertion(""));
        Assert.IsNull(ClientAuthenticator.TryReadClientIdFromAssertion("two.parts"));
        Assert.IsNull(ClientAuthenticator.TryReadClientIdFromAssertion("!!!not-base64!!!.AAAA.BBB"));
    }

    /// <summary>Builds a structurally valid (unsigned) JWT string with the given iss/sub claims.</summary>
    private static string FakeJwt(string? iss, string? sub)
    {
        static string B64Url(string json) => Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(json);
        var header = B64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\"}");
        var claims = new List<string>();
        if (iss is not null) claims.Add($"\"iss\":\"{iss}\"");
        if (sub is not null) claims.Add($"\"sub\":\"{sub}\"");
        var payload = B64Url("{" + string.Join(",", claims) + "}");
        var signature = B64Url("c2ln");
        return $"{header}.{payload}.{signature}";
    }
}
