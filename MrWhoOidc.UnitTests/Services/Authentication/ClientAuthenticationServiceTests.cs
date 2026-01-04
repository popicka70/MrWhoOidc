using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authentication;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.UnitTests.Services.Authentication;

[TestClass]
public class ClientAuthenticationServiceTests
{
    private Mock<IClientStore> _clientStoreMock = null!;
    private Mock<IClientAssertionValidator> _assertionValidatorMock = null!;
    private Mock<ILogger<ClientAuthenticationService>> _loggerMock = null!;
    private Mock<IOptions<AuthOptions>> _authOptionsMock = null!;
    private ClientAuthenticationService _service = null!;

    [TestInitialize]
    public void Initialize()
    {
        _clientStoreMock = new Mock<IClientStore>();
        _assertionValidatorMock = new Mock<IClientAssertionValidator>();
        _loggerMock = new Mock<ILogger<ClientAuthenticationService>>();
        _authOptionsMock = new Mock<IOptions<AuthOptions>>();
        _authOptionsMock.Setup(o => o.Value).Returns(new AuthOptions());

        _service = new ClientAuthenticationService(
            _clientStoreMock.Object,
            _assertionValidatorMock.Object,
            _authOptionsMock.Object,
            _loggerMock.Object);
    }

    [TestMethod]
    public async Task AuthenticateAsync_UnknownClient_ReturnsFailure()
    {
        // Arrange
        _clientStoreMock.Setup(s => s.FindByClientIdAsync("unknown", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client?)null);

        var input = new ClientCredentialInput("unknown");

        // Act
        var result = await _service.AuthenticateAsync(input);

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("unauthorized_client", result.Error);
    }

    [TestMethod]
    public async Task AuthenticateAsync_ValidSecret_ReturnsSuccess()
    {
        // Arrange
        var client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "client1" };
        _clientStoreMock.Setup(s => s.FindByClientIdAsync("client1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _clientStoreMock.Setup(s => s.ValidateClientSecretAsync("client1", "secret1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var input = new ClientCredentialInput(ClientId: "client1", ClientSecret: "secret1");

        // Act
        var result = await _service.AuthenticateAsync(input);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(client, result.Client);
    }

    [TestMethod]
    public async Task AuthenticateAsync_InvalidSecret_ReturnsFailure()
    {
        // Arrange
        var client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "client1" };
        _clientStoreMock.Setup(s => s.FindByClientIdAsync("client1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _clientStoreMock.Setup(s => s.ValidateClientSecretAsync("client1", "wrong", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var input = new ClientCredentialInput(ClientId: "client1", ClientSecret: "wrong");

        // Act
        var result = await _service.AuthenticateAsync(input);

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("unauthorized_client", result.Error);
    }

    [TestMethod]
    public async Task AuthenticateAsync_ValidAssertion_ReturnsSuccess()
    {
        // Arrange
        var client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "client1", AllowPrivateKeyJwt = true };
        _clientStoreMock.Setup(s => s.FindByClientIdAsync("client1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        _assertionValidatorMock.Setup(v => v.ValidateAsync("client1", "assertion", "https://op/token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var input = new ClientCredentialInput(
            ClientId: "client1",
            ClientAssertionType: OAuthConstants.ClientAssertionTypes.JwtBearer,
            ClientAssertion: "assertion",
            EndpointUrl: "https://op/token");

        // Act
        var result = await _service.AuthenticateAsync(input);

        // Assert
        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task AuthenticateAsync_Mtls_Success()
    {
        // Arrange
        var client = new MrWhoOidc.Auth.Persistence.Client 
        { 
            ClientId = "client1", 
            M2MMtlsThumbprintsJson = "[\"thumb1\"]"
        };
        _clientStoreMock.Setup(s => s.FindByClientIdAsync("client1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        var input = new ClientCredentialInput(
            ClientId: "client1", 
            Usage: ClientAuthenticationUsage.TokenEndpoint, 
            GrantType: OAuthConstants.GrantTypes.ClientCredentials, 
            MtlsThumbprint: "thumb1");
        
        var result = await _service.AuthenticateAsync(input);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(client, result.Client);
    }

    [TestMethod]
    public async Task AuthenticateAsync_Mtls_Mismatch_Returns_InvalidClient_MtlsRequired()
    {
        // Arrange
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "client1",
            M2MMtlsThumbprintsJson = "[\"thumb1\"]"
        };

        _clientStoreMock.Setup(s => s.FindByClientIdAsync("client1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        var input = new ClientCredentialInput(
            ClientId: "client1",
            Usage: ClientAuthenticationUsage.TokenEndpoint,
            GrantType: OAuthConstants.GrantTypes.ClientCredentials,
            MtlsThumbprint: "not-thumb1");

        // Act
        var result = await _service.AuthenticateAsync(input);

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("invalid_client", result.Error);
        Assert.AreEqual("mtls_required", result.ErrorDescription);
    }
}




