using Moq;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;

namespace MrWhoOidc.UnitTests.Helpers;

public static class TestTokenValidatorFactory
{
    public static ITokenValidator Create(IKeyStore keyStore)
    {
        var mockProvider = new Mock<ICachedKeyProvider>();
        mockProvider.Setup(p => p.GetPublicJwksAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async (ct) => {
                var jwks = await keyStore.GetPublicJwksAsync(ct);
                return jwks.Select(j => new JsonWebKey(j.ToJson(includePrivate: false))).ToList();
            });
        return new TokenValidator(mockProvider.Object);
    }
}

