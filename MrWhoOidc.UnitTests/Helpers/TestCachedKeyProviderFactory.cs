using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;
using Moq;
using System.Linq;
using System.Threading;

namespace MrWhoOidc.UnitTests.Helpers;

public static class TestCachedKeyProviderFactory
{
    public static ICachedKeyProvider Create(IKeyStore keyStore)
    {
        var mockProvider = new Mock<ICachedKeyProvider>();
        mockProvider
            .Setup(p => p.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async (ct) => (SecurityKey)await keyStore.GetActiveSigningKeyAsync(ct).ConfigureAwait(false));

        mockProvider
            .Setup(p => p.GetPublicJwksAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async (ct) =>
            {
                    var jwks = await keyStore.GetPublicJwksAsync(ct: ct).ConfigureAwait(false);
                return jwks.ToList().AsReadOnly();
            });

        return mockProvider.Object;
    }
}
