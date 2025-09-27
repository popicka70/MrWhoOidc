using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using System.Security.Claims;
using MrWhoOidc.WebAuth.Handlers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http;
using MrWhoOidc.WebAuth.Observability;
using System;
using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class LogoutPromptFlowTests
{
    private static AuthDbContext BuildDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new AuthDbContext(opts);
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public TestHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name = null!) => _client;
    }

    [TestMethod]
    public async Task LogoutEntry_RedirectsToPrompt_WithStyle()
    {
        var db = BuildDb();
        db.IdentityProviders.Add(new IdentityProvider { Name = "google", ConfigJson = "{\"Authority\":\"https://issuer\",\"ClientId\":\"abc\"}" });
        db.SaveChanges();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dp = new EphemeralDataProtectionProvider();
    var svc = new UpstreamLogoutService(cache, Options.Create(new FederatedLogoutOptions { Enabled = true, StateTtlSeconds = 60 }), dp, new NullLogger<UpstreamLogoutService>(), db, new TestHttpClientFactory(new HttpClient(new TestHttpHandler())), new NoopAuditSink());
    var keyStore = new KeyStore(db); // uses in-memory DB
    var handler = new LogoutHandler(db, keyStore, new NullLogger<LogoutHandler>(), new OidcMetrics(), new NoopAuditSink(), svc, Options.Create(new FederatedLogoutOptions { Enabled = true, StateTtlSeconds = 60 }));
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("idp", "google") }, "cookie"));
        ctx.Request.QueryString = new QueryString("?returnUrl=%2Fhome&style=dark");
        var result = await handler.LogoutEntryAsync(ctx);
    // Generic redirect result (Status 302) -> reflectively check Url via pattern matching
    Assert.IsTrue(result is Microsoft.AspNetCore.Http.IResult);
    var rdProp = result.GetType().GetProperty("Url");
    Assert.IsNotNull(rdProp, "Redirect result missing Url property");
    var url = rdProp!.GetValue(result) as string;
    StringAssert.Contains(url!, "/Logout/Prompt");
    StringAssert.Contains(url!, "style=dark");
    }
}