using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.WebAuth.Handlers.Introspection;
using MrWhoOidc.Security;
using PersistedClient = MrWhoOidc.Auth.Persistence.Client;
using PersistedToken = MrWhoOidc.Auth.Persistence.Token;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Opaque access and refresh token introspection returned the internal user id as sub (and username)
/// even for pairwise clients, while the JWT form of the same token carries the pairwise subject.
/// </summary>
[TestClass]
public sealed class IntrospectionPairwiseSubjectTests
{
    private static async Task<(AuthDbContext Db, PersistedClient Client, Guid UserId, string Raw)> SeedAsync(string type)
    {
        var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var client = new PersistedClient
        {
            ClientId = "pw-app",
            SubjectType = OidcConstants.SubjectTypes.Pairwise,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://pw.example.com/cb" }),
            IntrospectionResponseFieldsJson = JsonSerializer.Serialize(new[] { "active", "sub", "username" })
        };
        var userId = Guid.NewGuid();
        const string raw = "opaque-token-value";
        db.Clients.Add(client);
        db.Tokens.Add(new PersistedToken
        {
            Type = type,
            TokenHash = raw.ComputeTokenHash(),
            UserId = userId,
            ClientId = "pw-app",
            Audience = "api",
            ScopesJson = "[\"openid\"]",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });
        await db.SaveChangesAsync();
        return (db, client, userId, raw);
    }

    private static PairwiseSubjectService Pairwise(AuthDbContext db)
        => new(db, new SectorIdentifierResolver(new Mock<IHttpClientFactory>().Object), NullLogger<PairwiseSubjectService>.Instance);

    private static IntrospectionContext Context(PersistedClient client, string raw, string? hint = null) => new()
    {
        Request = new IntrospectionRequest(raw, hint, client.ClientId, "secret", null, null),
        Client = client,
        Issuer = "https://op.example.com",
        Endpoint = "https://op.example.com/introspect",
        HttpContext = new DefaultHttpContext(),
        ClientBucket = "b",
        MetricTags = []
    };

    [TestMethod]
    public async Task OpaqueAccessToken_OfPairwiseClient_IntrospectsWithPairwiseSub()
    {
        var (db, client, userId, raw) = await SeedAsync("access");
        using var _ = db;
        var authOptions = Options.Create(new AuthOptions());
        var introspector = new OpaqueTokenIntrospector(db,
            new MrWhoOidc.WebAuth.Handlers.Introspection.DPoPValidator(new Mock<IDPoPValidator>().Object, new Mock<IDPoPReplayCache>().Object, new Mock<IDPoPNonceStore>().Object),
            new AudiencePolicy(authOptions), new ResponseShaper(authOptions), NullLogger<OpaqueTokenIntrospector>.Instance, Pairwise(db));

        var (response, error) = await introspector.IntrospectAsync(Context(client, raw));

        Assert.IsNull(error);
        var expected = await Pairwise(db).GetSubjectAsync(client, userId);
        Assert.AreEqual(expected, response!["sub"]);
        Assert.AreEqual(expected, response["username"]);
        Assert.AreNotEqual(userId.ToString(), response["sub"]);
    }

    [TestMethod]
    public async Task RefreshToken_OfPairwiseClient_IntrospectsWithPairwiseSub()
    {
        var (db, client, userId, raw) = await SeedAsync("refresh");
        using var _ = db;
        var store = new Mock<IClientStore>();
        store.Setup(s => s.FindByClientIdAsync("pw-app", It.IsAny<CancellationToken>())).ReturnsAsync(client);
        var authOptions = Options.Create(new AuthOptions { AllowRefreshTokenIntrospection = true });
        var introspector = new RefreshTokenIntrospector(db, store.Object, new ResponseShaper(authOptions), authOptions,
            NullLogger<RefreshTokenIntrospector>.Instance, Pairwise(db));

        var (response, error) = await introspector.IntrospectAsync(Context(client, raw, "refresh_token"));

        Assert.IsNull(error);
        Assert.AreEqual(await Pairwise(db).GetSubjectAsync(client, userId), response!["sub"]);
        Assert.AreNotEqual(userId.ToString(), response["username"]);
    }
}
