using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Regression tests for C3/C4 of the 2026-10-04 assessment: PAR request_uri replay,
/// cross-client redemption, front-channel overrides and RequirePar bypass.
/// </summary>
[TestClass]
public sealed class ParSecurityRegressionTests
{
    private static readonly AuthorizeRequest Pushed = new(
        response_type: "code",
        client_id: "owner",
        redirect_uri: "https://owner.example/cb",
        scope: "openid",
        state: "pushed-state");

    [TestMethod]
    public void EfStore_MarkConsumed_SucceedsExactlyOnce()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var store = new EfPushedAuthorizationRequestStore(db, Options.Create(new AuthOptions()), MockTenantAccessor.CreateWithDefaultTenant());
        var id = Guid.NewGuid().ToString("N");
        store.Create(id, Pushed, "owner", TimeSpan.FromMinutes(5), null);

        Assert.IsTrue(store.MarkConsumedById(id));
        Assert.IsFalse(store.MarkConsumedById(id), "a request_uri must not be redeemable twice");
        Assert.IsNull(store.TryGetById(id));
    }

    [TestMethod]
    public void EfStore_MarkConsumed_RejectsFullRequestUri()
    {
        // The old AuthorizeHandler passed "https://issuer/par/{id}"; that must not silently "succeed".
        using var db = TestDataSeeder.CreateInMemoryDb();
        var store = new EfPushedAuthorizationRequestStore(db, Options.Create(new AuthOptions()), MockTenantAccessor.CreateWithDefaultTenant());
        var id = Guid.NewGuid().ToString("N");
        store.Create(id, Pushed, "owner", TimeSpan.FromMinutes(5), null);

        Assert.IsFalse(store.MarkConsumedById($"https://op.example/par/{id}"));
        Assert.IsTrue(store.MarkConsumedById(id));
    }

    [TestMethod]
    public void InMemoryStore_MarkConsumed_SucceedsExactlyOnce()
    {
        var store = new InMemoryPushedAuthorizationRequestStore();
        store.Create("abc", Pushed, "owner", TimeSpan.FromMinutes(5), null);

        Assert.IsTrue(store.MarkConsumedById("abc"));
        Assert.IsFalse(store.MarkConsumedById("abc"));
    }

    [TestMethod]
    public async Task Resolver_RejectsPar_RedeemedByDifferentClient()
    {
        var (resolver, _) = CreateResolver();
        var result = await resolver.ResolveAsync(Query(("client_id", "attacker")), "urn:ietf:params:oauth:request_uri:abc", null, "https://op.example");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request", result.Error);
    }

    [TestMethod]
    public async Task Resolver_RejectsPar_WithoutClientId()
    {
        var (resolver, _) = CreateResolver();
        var result = await resolver.ResolveAsync(Query(), "urn:ietf:params:oauth:request_uri:abc", null, "https://op.example");

        Assert.IsFalse(result.IsValid);
    }

    [TestMethod]
    public async Task Resolver_IgnoresFrontChannelState_ForPar()
    {
        var (resolver, _) = CreateResolver();
        var result = await resolver.ResolveAsync(Query(("client_id", "owner"), ("state", "injected")), "urn:ietf:params:oauth:request_uri:abc", null, "https://op.example");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("pushed-state", result.Request!.state);
        Assert.AreEqual("abc", result.ParId);
    }

    [TestMethod]
    public async Task Resolver_EnforcesClientRequirePar_ForPlainQuery()
    {
        var (resolver, db) = CreateResolver();
        db.Clients.Add(new ClientEntity { ClientId = "owner", RequirePar = true, TenantId = Guid.NewGuid() });
        await db.SaveChangesAsync();

        var result = await resolver.ResolveAsync(
            Query(("client_id", "owner"), ("response_type", "code"), ("redirect_uri", "https://owner.example/cb"), ("scope", "openid")),
            null, null, "https://op.example");

        Assert.IsFalse(result.IsValid, "a client with RequirePar must not be able to use a plain query request");
    }

    [TestMethod]
    public async Task Resolver_EnforcesGlobalRequirePar_ForPlainQuery()
    {
        var (resolver, _) = CreateResolver(new AuthOptions { RequirePar = true });

        var result = await resolver.ResolveAsync(Query(("client_id", "other"), ("response_type", "code")), null, null, "https://op.example");

        Assert.IsFalse(result.IsValid);
    }

    private static IEnumerable<KeyValuePair<string, string>> Query(params (string Key, string Value)[] pairs)
        => pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).ToList();

    private static (AuthorizeRequestResolver Resolver, AuthDbContext Db) CreateResolver(AuthOptions? options = null)
    {
        var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var parStore = new InMemoryPushedAuthorizationRequestStore();
        parStore.Create("abc", Pushed, "owner", TimeSpan.FromMinutes(5), null);
        var resolver = new AuthorizeRequestResolver(
            new NoopRequestObjectValidator(),
            parStore,
            db,
            Options.Create(options ?? new AuthOptions()),
            NullLogger<AuthorizeRequestResolver>.Instance);
        return (resolver, db);
    }

    private sealed class NoopRequestObjectValidator : IRequestObjectValidator
    {
        public Task<RequestObjectValidationResult> ValidateAsync(string requestJwt, string expectedAudience, CancellationToken ct = default)
            => Task.FromResult(new RequestObjectValidationResult { IsValid = false, Error = "not used" });
    }
}
