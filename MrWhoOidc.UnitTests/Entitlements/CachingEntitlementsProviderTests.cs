using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Entitlements.Contracts;
using MrWhoOidc.Auth.Entitlements.Options;

namespace MrWhoOidc.UnitTests.Entitlements;

[TestClass]
public class CachingEntitlementsProviderTests
{
    [TestMethod]
    public async Task GetEffectiveEntitlementsAsync_WhenClientThrowsException_CachesNegativeSentinel()
    {
        // Arrange
        var cache = new MemoryCache(new MemoryCacheOptions());
        var clientMock = new Mock<ILicensingEntitlementsClient>();
        var optionsMock = new Mock<IOptions<LicensingIntegrationOptions>>();
        var logger = NullLogger<CachingEntitlementsProvider>.Instance;

        optionsMock.Setup(o => o.Value).Returns(new LicensingIntegrationOptions
        {
            Enabled = true,
            CacheTtlMinutes = 5,
            NegativeCacheTtlSeconds = 10
        });

        // Set up client to throw exception
        clientMock
            .Setup(c => c.ResolveEffectiveEntitlementsAsync(It.IsAny<EffectiveEntitlementsRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated API failure"));

        var provider = new CachingEntitlementsProvider(cache, clientMock.Object, optionsMock.Object, logger);

        string subjectId = "sub1";
        string tenantId = "tenant1";
        string productKey = "prod1";
        string issuer = "https://issuer.com";

        // Act 1: Initial call, client should throw, provider should catch and set negative cache
        var result1 = await provider.GetEffectiveEntitlementsAsync(
            subjectId,
            tenantId,
            new[] { productKey },
            issuer);

        // Assert 1
        Assert.IsNotNull(result1);
        Assert.IsFalse(result1.ContainsKey(productKey), "Result should not contain entitlement on failure.");
        clientMock.Verify(c => c.ResolveEffectiveEntitlementsAsync(It.IsAny<EffectiveEntitlementsRequest>(), issuer, It.IsAny<CancellationToken>()), Times.Once);

        // Act 2: Second call, should hit negative cache and NOT call the client again
        var result2 = await provider.GetEffectiveEntitlementsAsync(
            subjectId,
            tenantId,
            new[] { productKey },
            issuer);

        // Assert 2
        Assert.IsNotNull(result2);
        Assert.IsFalse(result2.ContainsKey(productKey), "Result should not contain entitlement from cache.");

        // The mock should still only have been called once
        clientMock.Verify(c => c.ResolveEffectiveEntitlementsAsync(It.IsAny<EffectiveEntitlementsRequest>(), issuer, It.IsAny<CancellationToken>()), Times.Once);
    }
}
