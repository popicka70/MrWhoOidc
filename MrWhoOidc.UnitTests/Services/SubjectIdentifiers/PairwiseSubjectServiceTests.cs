using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;

namespace MrWhoOidc.UnitTests.Services.SubjectIdentifiers;

[TestClass]
public sealed class PairwiseSubjectServiceTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public async Task GetSubjectAsync_Pairwise_IsStable_ForSameUserAndSector()
    {
        using var db = CreateDb();
        var resolver = new SectorIdentifierResolver(new StubHttpClientFactory());
        var svc = new PairwiseSubjectService(db, resolver, NullLogger<PairwiseSubjectService>.Instance);

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            TenantId = tenantId,
            SubjectType = OidcConstants.SubjectTypes.Pairwise,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://app.example.com/signin-oidc" })
        };

        var sub1 = await svc.GetSubjectAsync(client, userId);
        var sub2 = await svc.GetSubjectAsync(client, userId);

        Assert.AreEqual(sub1, sub2);
        Assert.AreNotEqual(userId.ToString(), sub1);

        var count = await db.PairwiseSubjectIdentifiers.CountAsync();
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public async Task GetSubjectAsync_Public_ReturnsUserIdString()
    {
        using var db = CreateDb();
        var resolver = new SectorIdentifierResolver(new StubHttpClientFactory());
        var svc = new PairwiseSubjectService(db, resolver, NullLogger<PairwiseSubjectService>.Instance);

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            TenantId = Guid.NewGuid(),
            SubjectType = OidcConstants.SubjectTypes.Public,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://app.example.com/signin-oidc" })
        };

        var userId = Guid.NewGuid();
        var sub = await svc.GetSubjectAsync(client, userId);

        Assert.AreEqual(userId.ToString(), sub);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient(new ThrowingHandler());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => throw new InvalidOperationException("HTTP should not be used by these tests.");
    }
}
