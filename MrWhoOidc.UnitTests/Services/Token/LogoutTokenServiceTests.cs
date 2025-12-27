using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Protocols;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.UnitTests.Services.Token;

[TestClass]
public class LogoutTokenServiceTests
{
    private Mock<ICachedKeyProvider> _keyProviderMock = null!;
    private LogoutTokenService _service = null!;

    [TestInitialize]
    public void Initialize()
    {
        _keyProviderMock = new Mock<ICachedKeyProvider>();
        _service = new LogoutTokenService(_keyProviderMock.Object);
    }

    [TestMethod]
    public async Task CreateLogoutTokenAsync_Success()
    {
        var rsa = System.Security.Cryptography.RSA.Create();
        var key = new RsaSecurityKey(rsa) { KeyId = "key1" };
        _keyProviderMock.Setup(s => s.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(key);

        var result = await _service.CreateLogoutTokenAsync("https://issuer", "client1", "sub1", "sid1");

        Assert.IsNotNull(result);
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(result);

        Assert.AreEqual("https://issuer", token.Issuer);
        Assert.IsTrue(token.Audiences.Contains("client1"));
        Assert.AreEqual("sub1", token.Subject);
        Assert.AreEqual("sid1", token.Claims.FirstOrDefault(c => c.Type == "sid")?.Value);
        Assert.AreEqual("logout+jwt", token.Header["typ"]);
    }

    [TestMethod]
    public async Task CreateLogoutTokenAsync_NoSigningKey_Throws()
    {
        _keyProviderMock.Setup(s => s.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((RsaSecurityKey?)null);

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () => await _service.CreateLogoutTokenAsync("https://issuer", "client1", "sub1", "sid1"));
    }
}



