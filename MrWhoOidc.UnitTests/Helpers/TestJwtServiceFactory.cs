using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace MrWhoOidc.UnitTests.Helpers;

public static class TestJwtServiceFactory
{
    public static IJwtService Create(IKeyStore keyStore)
    {
        var mockProvider = new Mock<ICachedKeyProvider>();
        mockProvider.Setup(p => p.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async (ct) => 
            {
                var jwk = await keyStore.GetActiveSigningKeyAsync(ct);
                return new JsonWebKey(jwk.ToJson(includePrivate: true));
            });
        
        return new JwtService(mockProvider.Object);
    }
}
