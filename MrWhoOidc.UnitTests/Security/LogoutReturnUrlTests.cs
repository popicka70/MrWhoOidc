using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Handlers.Logout;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Logout return URLs (2026-10-04 assessment): backslash/bare-host values must not survive, and genuine
/// local paths must be preserved (Uri.TryCreate(Absolute) treated "/x" as file:///x on Linux).
/// </summary>
[TestClass]
public sealed class LogoutReturnUrlTests
{
    [TestMethod]
    [DataRow("/account/profile?tab=1", "/account/profile?tab=1")]
    [DataRow("/\\evil.com", "/")]
    [DataRow("//evil.com", "/")]
    [DataRow("evil.com", "/")]
    [DataRow("https://evil.com", "/")]
    public async Task LocalLogout_RedirectsOnlyToLocalPaths(string returnUrl, string expected)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(Mock.Of<IAuthenticationService>()).BuildServiceProvider() };

        var result = await new LocalLogoutHandler().ExecuteAsync(ctx, returnUrl);

        Assert.AreEqual(expected, ((RedirectHttpResult)result).Url);
    }

    [TestMethod]
    [DataRow("/account", "/account")]
    [DataRow("/\\evil.com", "/")]
    [DataRow("evil.com", "/")]
    public async Task FederatedLogout_CallbackReturnUrl_IsLocalOnly(string returnUrl, string expected)
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        db.IdentityProviders.Add(new IdentityProvider { Name = "foo", ConfigJson = "{\"Authority\":\"https://issuer.test\",\"ClientId\":\"abc\"}" });
        db.SaveChanges();
        var http = new HttpClient(new StaticHandler("{\"end_session_endpoint\":\"https://issuer.test/endsession\"}"));
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient(It.IsAny<string>()) == http);
        var svc = new UpstreamLogoutService(new MemoryCache(new MemoryCacheOptions()), Options.Create(new FederatedLogoutOptions()),
            new EphemeralDataProtectionProvider(), NullLogger<UpstreamLogoutService>.Instance, db, factory, new NoopAuditSink());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("idp", "foo") }, "cookie"));

        var redirect = await svc.BuildFederatedRedirectAsync(principal, null, null, "https://local.app", returnUrl, null, null, CancellationToken.None);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(redirect.RedirectUrl!).Query)["state"];
        var validation = await svc.ValidateCallbackAsync(state, CancellationToken.None);

        Assert.IsTrue(validation.Valid);
        Assert.AreEqual(expected, validation.ReturnUrl);
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
