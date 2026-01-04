using Moq;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;
using System.Linq;
using System.Threading;

namespace MrWhoOidc.UnitTests.Helpers;

public static class TestTokenValidatorFactory
{
    public static ITokenValidator Create(IKeyStore keyStore)
    {
        var mockProvider = new Mock<ICachedKeyProvider>();
        mockProvider.Setup(p => p.GetPublicJwksAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async (ct) => {
                    var jwks = await keyStore.GetPublicJwksAsync(ct: ct).ConfigureAwait(false);
                return jwks.ToList().AsReadOnly();
            });
        return new TokenValidator(mockProvider.Object);
    }
}

