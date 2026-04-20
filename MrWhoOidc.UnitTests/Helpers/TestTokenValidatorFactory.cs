using Moq;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;
using System.Linq;
using System.Threading;

namespace MrWhoOidc.UnitTests.Helpers;

public static class TestTokenValidatorFactory
{
    public static ITokenValidator Create(IKeyStore keyStore)
        => Create(
            keyStore,
            new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options),
            MockTenantAccessor.CreateSingleTenantMode());

    public static ITokenValidator Create(IKeyStore keyStore, AuthDbContext db, ITenantAccessor? tenantAccessor = null)
    {
        var mockProvider = new Mock<ICachedKeyProvider>();
        mockProvider.Setup(p => p.GetPublicJwksAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async (ct) =>
            {
                var jwks = await keyStore.GetPublicJwksAsync(ct: ct).ConfigureAwait(false);
                return jwks.ToList().AsReadOnly();
            });
        return new TokenValidator(mockProvider.Object, db, tenantAccessor ?? MockTenantAccessor.CreateSingleTenantMode());
    }
}

