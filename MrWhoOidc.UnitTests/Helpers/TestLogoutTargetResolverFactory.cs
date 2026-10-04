using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.WebAuth.Handlers.Logout;

namespace MrWhoOidc.UnitTests.Helpers;

public static class TestLogoutTargetResolverFactory
{
    /// <summary>Creates a resolver that verifies hints against <paramref name="keyStore"/> and uses public subjects.</summary>
    public static LogoutTargetResolver Create(AuthDbContext db, IKeyStore keyStore)
    {
        var keys = new Mock<ICachedKeyProvider>();
        keys.Setup(p => p.GetPublicJwksAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async ct => (await keyStore.GetPublicJwksAsync(ct: ct).ConfigureAwait(false)).ToList().AsReadOnly());

        var pairwise = new Mock<IPairwiseSubjectService>();
        pairwise.Setup(p => p.GetSubjectAsync(It.IsAny<ClientEntity>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns<ClientEntity, Guid, CancellationToken>((_, userId, _) => Task.FromResult(userId.ToString()));

        return new LogoutTargetResolver(db, keys.Object, pairwise.Object, NullLogger<LogoutTargetResolver>.Instance);
    }
}
