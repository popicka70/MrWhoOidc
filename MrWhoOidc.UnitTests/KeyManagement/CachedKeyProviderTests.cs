using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Crypto;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.UnitTests.KeyManagement;

[TestClass]
public sealed class CachedKeyProviderTests
{
    private Mock<IKeyStore> _keyStoreMock = null!;
    private Mock<ITenantAccessor> _tenantAccessorMock = null!;
    private Mock<IServiceScopeFactory> _scopeFactoryMock = null!;
    private Mock<IServiceScope> _scopeMock = null!;
    private Mock<IServiceProvider> _serviceProviderMock = null!;
    private Mock<IHttpContextAccessor> _httpContextAccessorMock = null!;
    private CachedKeyProvider _provider = null!;
    private readonly Guid _tenantId = Guid.NewGuid();

    [TestInitialize]
    public void Setup()
    {
        _keyStoreMock = new Mock<IKeyStore>();
        _tenantAccessorMock = new Mock<ITenantAccessor>();
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _scopeMock = new Mock<IServiceScope>();
        _serviceProviderMock = new Mock<IServiceProvider>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();

        // In unit tests we use the fallback scope-based resolution.
        _httpContextAccessorMock.Setup(a => a.HttpContext).Returns((HttpContext?)null);

        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _serviceProviderMock.Setup(p => p.GetService(typeof(ITenantAccessor))).Returns(_tenantAccessorMock.Object);
        _serviceProviderMock.Setup(p => p.GetService(typeof(IKeyStore))).Returns(_keyStoreMock.Object);
        
        _tenantAccessorMock.Setup(a => a.CurrentTenant).Returns(new TenantContext { TenantId = _tenantId, Slug = "test" });
        
        _provider = new CachedKeyProvider(_scopeFactoryMock.Object, _httpContextAccessorMock.Object);
    }

    [TestMethod]
    public async Task GetActiveSigningKeyAsync_CachesResult()
    {
        // Arrange
        var jwk = new RsaJwk { Kid = "k1", Alg = "RS256", Kty = "RSA", N = "n", E = "e", D = "d" };
        _keyStoreMock.Setup(k => k.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(jwk);

        // Act
        var key1 = await _provider.GetActiveSigningKeyAsync();
        var key2 = await _provider.GetActiveSigningKeyAsync();

        // Assert
        Assert.IsNotNull(key1);
        Assert.AreEqual(key1, key2);
        _keyStoreMock.Verify(k => k.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task InvalidateCache_ClearsCache()
    {
        // Arrange
        var jwk = new RsaJwk { Kid = "k1", Alg = "RS256", Kty = "RSA", N = "n", E = "e", D = "d" };
        _keyStoreMock.Setup(k => k.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(jwk);

        // Act
        await _provider.GetActiveSigningKeyAsync();
        _provider.InvalidateCache();
        await _provider.GetActiveSigningKeyAsync();

        // Assert
        _keyStoreMock.Verify(k => k.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
