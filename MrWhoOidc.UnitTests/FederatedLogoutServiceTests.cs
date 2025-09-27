using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net;
using System;
using System.Collections.Generic;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class FederatedLogoutServiceTests
{
    private static AuthDbContext BuildDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static IOptions<FederatedLogoutOptions> FedOpts(bool enabled = true) => Options.Create(new FederatedLogoutOptions { Enabled = enabled, StateTtlSeconds = 300 });

    private sealed class TestHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? OnSend { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(OnSend?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static (UpstreamLogoutService svc, AuthDbContext db, TestHandler handler, IDataProtector idTokProtector) BuildService(string discoJson, string authority, string providerName = "google")
    {
        var db = BuildDb();
        // Build minimal provider config JSON
        var cfgJson = $"{{\"Authority\":\"{authority}\",\"ClientId\":\"abc\"}}";
        db.IdentityProviders.Add(new IdentityProvider
        {
            Name = providerName,
            ConfigJson = cfgJson
        });
        db.SaveChanges();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var opts = FedOpts();
        var dp = new EphemeralDataProtectionProvider();
        var handler = new TestHandler
        {
            OnSend = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.IsNullOrEmpty(discoJson) ? "{}" : discoJson)
            }
        };
        var httpClientFactory = new TestHttpClientFactory(new HttpClient(handler));

        var svc = new UpstreamLogoutService(cache, opts, dp, new NullLogger<UpstreamLogoutService>(), db, httpClientFactory, new NoopAuditSink());
        var idTokProt = dp.CreateProtector("federated-logout-idtoken");
        return (svc, db, handler, idTokProt);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public TestHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name = null!) => _client;
    }

    private static ClaimsPrincipal BuildPrincipal(string idpName, string? issuer = null)
    {
        var claims = new List<Claim> { new("idp", idpName) };
        if (!string.IsNullOrEmpty(issuer)) claims.Add(new("ext_map_iss", issuer));
        var id = new ClaimsIdentity(claims, "cookie");
        return new ClaimsPrincipal(id);
    }

    [TestMethod]
    public async Task CanFederate_ReturnsFalse_WhenDisabled()
    {
        var db = BuildDb();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dp = new EphemeralDataProtectionProvider();
        var svc = new UpstreamLogoutService(cache, Options.Create(new FederatedLogoutOptions { Enabled = false }), dp, new NullLogger<UpstreamLogoutService>(), db, new TestHttpClientFactory(new HttpClient(new TestHandler())), new NoopAuditSink());
        var principal = BuildPrincipal("foo");
        var cap = await svc.CanFederateAsync(principal, CancellationToken.None);
        Assert.IsFalse(cap.CanFederate);
    }

    [TestMethod]
    public async Task CanFederate_ReturnsTrue_ForExistingProvider()
    {
        var authority = "https://example.com";
        var disco = "{\"end_session_endpoint\":\"https://example.com/logout-end\"}";
        var (svc, db, _, _) = BuildService(disco, authority, "foo");
        var principal = BuildPrincipal("foo");
        var cap = await svc.CanFederateAsync(principal, CancellationToken.None);
        Assert.IsTrue(cap.CanFederate);
        Assert.AreEqual("foo", cap.ProviderName);
    }

    [TestMethod]
    public async Task BuildFederatedRedirect_UsesPublishedEndSession()
    {
        var authority = "https://issuer.test";
        var disco = "{\"end_session_endpoint\":\"https://issuer.test/connect/endsession\"}";
        var (svc, db, _, prot) = BuildService(disco, authority, "foo");
        var rawIdToken = "header.payload.sig"; // fake structure
        var enc = prot.Protect(rawIdToken);
        var principal = BuildPrincipal("foo");
        var res = await svc.BuildFederatedRedirectAsync(principal, enc, "mysid", "https://local.app", "/return", null, null, CancellationToken.None);
        Assert.IsTrue(res.Success, res.FailureReason);
        StringAssert.Contains(res.RedirectUrl!, "connect/endsession");
        StringAssert.Contains(res.RedirectUrl!, "id_token_hint=");
        StringAssert.Contains(res.RedirectUrl!, "sid=mysid");
    }

    [TestMethod]
    public async Task BuildFederatedRedirect_FallsBackWhenDiscoveryMissing()
    {
        var authority = "https://fallback.test";
        var disco = "{}"; // no end_session_endpoint
        var (svc, db, _, _) = BuildService(disco, authority, "foo");
        var principal = BuildPrincipal("foo");
        var res = await svc.BuildFederatedRedirectAsync(principal, null, null, "https://local.app", null, null, null, CancellationToken.None);
        Assert.IsTrue(res.Success, res.FailureReason);
        // Heuristic chooses /v2/logout first
        StringAssert.Contains(res.RedirectUrl!, "/v2/logout");
    }

    [TestMethod]
    public async Task BuildFederatedRedirect_FailsOnDiscoveryHttpError()
    {
        var authority = "https://bad.test";
        var db = BuildDb();
        var cfgJson = $"{{\"Authority\":\"{authority}\",\"ClientId\":\"abc\"}}";
        db.IdentityProviders.Add(new IdentityProvider { Name = "foo", ConfigJson = cfgJson });
        db.SaveChanges();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dp = new EphemeralDataProtectionProvider();
        var handler = new TestHandler { OnSend = req => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        var svc = new UpstreamLogoutService(cache, FedOpts(), dp, new NullLogger<UpstreamLogoutService>(), db, new TestHttpClientFactory(new HttpClient(handler)), new NoopAuditSink());
        var principal = BuildPrincipal("foo");
        var res = await svc.BuildFederatedRedirectAsync(principal, null, null, "https://local.app", null, null, null, CancellationToken.None);
        Assert.IsFalse(res.Success);
        Assert.AreEqual("discovery_failed", res.FailureReason);
    }

    [TestMethod]
    public async Task ValidateCallback_RejectsInvalidState()
    {
        var authority = "https://issuer.test";
        var disco = "{\"end_session_endpoint\":\"https://issuer.test/endsession\"}";
        var (svc, db, _, _) = BuildService(disco, authority, "foo");
        var principal = BuildPrincipal("foo");
        var ok = await svc.BuildFederatedRedirectAsync(principal, null, null, "https://local.app", null, null, null, CancellationToken.None);
        var bad = await svc.ValidateCallbackAsync("notpresent", CancellationToken.None);
        Assert.IsFalse(bad.Valid);
        // Extract real state from redirect
        var realState = System.Web.HttpUtility.ParseQueryString(new Uri(ok.RedirectUrl!).Query)["state"];
        var good = await svc.ValidateCallbackAsync(realState, CancellationToken.None);
        Assert.IsTrue(good.Valid);
        // second use should fail (single-use)
        var again = await svc.ValidateCallbackAsync(realState, CancellationToken.None);
        Assert.IsFalse(again.Valid);
    }
}
