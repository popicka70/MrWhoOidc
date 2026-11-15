using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.TestSupport;
using System.Net;
using System.Text.Json;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Tests for the external request_uri fetcher (OIDC Core 6.2 / RFC 9101 §5.5): allow-list against the
/// client's registered request_uris, fragment handling, scheme rules and fetch behavior.
/// </summary>
[TestClass]
public sealed class RequestObjectUriFetcherTests
{
    private const string RequestUriBase = "https://www.conformance.example/test/a/alias/requesturi/0123456789abcdef";

    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    private static async Task<Guid> CreateTestTenant(AuthDbContext db)
    {
        var tenantId = GuidHelper.NewId();
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Test Tenant",
            Slug = "test",
            Status = TenantStatus.Active
        });
        await db.SaveChangesAsync();
        return tenantId;
    }

    /// <summary>A handler that records the requested URL and returns a fixed body.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? RequestedUrl { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrl = request.RequestUri;
            return await Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    private static async Task<Auth.Persistence.Client> SeedClientAsync(AuthDbContext db, Guid tenantId, string requestUrisJson)
    {
        var client = new Auth.Persistence.Client
        {
            Id = GuidHelper.NewId(),
            ClientId = "rp-client",
            ClientName = "RP",
            TenantId = tenantId,
            RequestUrisJson = requestUrisJson
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    [TestMethod]
    public async Task TryFetchAsync_RegisteredUri_WithFragment_Fetches_WithoutFragment()
    {
        // OIDC Core 6.2.1: the fragment is a hash of the referenced contents. It is compared
        // fragment-insensitively against the registered request_uris and stripped before the fetch.
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedClientAsync(db, tenantId, JsonSerializer.Serialize(new[] { $"{RequestUriBase}#6f2374f2" }));

        var handler = new StubHandler(_ => Ok("eyJhbGciOiJSUzI1NiJ9.eyJpc3MiOiJycCJ9.c2ln"));
        var fetcher = new RequestObjectUriFetcher(db, messageHandler: handler);

        // A DIFFERENT fragment on the wire must still match (the suite reuses the registered URI
        // with a per-request entropy fragment).
        var result = await fetcher.TryFetchAsync($"{RequestUriBase}#different-entropy", "rp-client");

        Assert.IsTrue(result.Success, $"expected success, got error {result.Error}: {result.ErrorDescription}");
        Assert.AreEqual("eyJhbGciOiJSUzI1NiJ9.eyJpc3MiOiJycCJ9.c2ln", result.RequestJwt);
        Assert.AreEqual("rp-client", result.ClientId);
        Assert.IsNotNull(handler.RequestedUrl);
        Assert.AreEqual(RequestUriBase, handler.RequestedUrl.AbsoluteUri, "the fragment must not be sent in the fetch");
    }

    [TestMethod]
    public async Task TryFetchAsync_UnregisteredUri_IsRejected()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedClientAsync(db, tenantId, JsonSerializer.Serialize(new[] { $"{RequestUriBase}/other#frag" }));

        var handler = new StubHandler(_ => Ok("a.b.c"));
        var fetcher = new RequestObjectUriFetcher(db, messageHandler: handler);

        var result = await fetcher.TryFetchAsync($"{RequestUriBase}#frag", "rp-client");

        Assert.IsFalse(result.Success);
        Assert.AreEqual("invalid_request", result.Error);
        Assert.IsNull(handler.RequestedUrl, "an unregistered URI must never be fetched (SSRF allow-list)");
    }

    [TestMethod]
    public async Task TryFetchAsync_NonHttpsNonLoopback_IsRejected()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedClientAsync(db, tenantId, JsonSerializer.Serialize(new[] { "http://intranet.example/request_object.jwt#frag" }));

        var handler = new StubHandler(_ => Ok("a.b.c"));
        var fetcher = new RequestObjectUriFetcher(db, messageHandler: handler);

        var result = await fetcher.TryFetchAsync("http://intranet.example/request_object.jwt#frag", "rp-client");

        Assert.IsFalse(result.Success);
        Assert.AreEqual("invalid_request", result.Error);
    }

    [TestMethod]
    public async Task TryFetchAsync_MissingClientId_IsRejected()
    {
        var db = CreateDb();
        var fetcher = new RequestObjectUriFetcher(db, messageHandler: new StubHandler(_ => Ok("a.b.c")));

        var result = await fetcher.TryFetchAsync($"{RequestUriBase}#frag", null);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("invalid_request", result.Error);
    }

    [TestMethod]
    public async Task TryFetchAsync_RequestUriOver512Bytes_IsRejected()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedClientAsync(db, tenantId, JsonSerializer.Serialize(new[] { $"{RequestUriBase}#frag" }));

        var handler = new StubHandler(_ => Ok("a.b.c"));
        var fetcher = new RequestObjectUriFetcher(db, messageHandler: handler);

        var longUri = $"{RequestUriBase}?pad=" + new string('a', 512);
        var result = await fetcher.TryFetchAsync(longUri, "rp-client");

        Assert.IsFalse(result.Success);
        Assert.AreEqual("invalid_request", result.Error);
    }

    [TestMethod]
    public async Task TryFetchAsync_NonJwtBody_IsRejected()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedClientAsync(db, tenantId, JsonSerializer.Serialize(new[] { $"{RequestUriBase}#frag" }));

        var handler = new StubHandler(_ => Ok("<html>not a request object</html>"));
        var fetcher = new RequestObjectUriFetcher(db, messageHandler: handler);

        var result = await fetcher.TryFetchAsync($"{RequestUriBase}#frag", "rp-client");

        Assert.IsFalse(result.Success);
        Assert.AreEqual("invalid_request_object", result.Error);
    }

    [TestMethod]
    public async Task TryFetchAsync_HttpErrorStatus_IsRejected()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedClientAsync(db, tenantId, JsonSerializer.Serialize(new[] { $"{RequestUriBase}#frag" }));

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var fetcher = new RequestObjectUriFetcher(db, messageHandler: handler);

        var result = await fetcher.TryFetchAsync($"{RequestUriBase}#frag", "rp-client");

        Assert.IsFalse(result.Success);
        Assert.AreEqual("invalid_request_object", result.Error);
    }

    [TestMethod]
    public async Task TryFetchAsync_UnknownClient_IsRejected()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        await SeedClientAsync(db, tenantId, JsonSerializer.Serialize(new[] { $"{RequestUriBase}#frag" }));

        var handler = new StubHandler(_ => Ok("a.b.c"));
        var fetcher = new RequestObjectUriFetcher(db, messageHandler: handler);

        var result = await fetcher.TryFetchAsync($"{RequestUriBase}#frag", "somebody-else");

        Assert.IsFalse(result.Success);
        Assert.AreEqual("invalid_request", result.Error);
        Assert.IsNull(handler.RequestedUrl, "no fetch for an unknown client");
    }
}
