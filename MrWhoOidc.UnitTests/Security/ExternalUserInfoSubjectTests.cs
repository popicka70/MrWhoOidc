using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.WebAuth.Handlers.External;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// OIDC Core 5.3.2 (2026-10-04 assessment): the upstream userinfo <c>sub</c> must match the ID token <c>sub</c>.
/// </summary>
[TestClass]
public sealed class ExternalUserInfoSubjectTests
{
    private static ExternalOidcTokenExchangeService Create(string userInfoJson)
    {
        var http = new HttpClient(new StaticHandler(userInfoJson));
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient(It.IsAny<string>()) == http);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Testing:AllowLocalExternalOidcHttp"] = "true",
            ["Environment"] = "Testing"
        }).Build();
        return new ExternalOidcTokenExchangeService(factory, config, NullLogger<ExternalOidcTokenExchangeService>.Instance);
    }

    [TestMethod]
    public async Task UserInfo_WithDifferentSub_IsFlagged_AndNotUsed()
    {
        var svc = Create("{\"sub\":\"someone-else\",\"email\":\"victim@example.com\",\"email_verified\":true}");

        var info = await svc.EnrichUserInfoAsync(new UserInfo { Subject = "user-1" }, "at", "http://up/userinfo", CancellationToken.None);

        Assert.IsTrue(info.UserInfoSubjectMismatch);
        Assert.AreEqual("user-1", info.Subject);
        Assert.IsNull(info.Email);
    }

    [TestMethod]
    public async Task UserInfo_WithMatchingSub_EnrichesClaims()
    {
        var svc = Create("{\"sub\":\"user-1\",\"email\":\"user1@example.com\",\"email_verified\":true}");

        var info = await svc.EnrichUserInfoAsync(new UserInfo { Subject = "user-1" }, "at", "http://up/userinfo", CancellationToken.None);

        Assert.IsFalse(info.UserInfoSubjectMismatch);
        Assert.AreEqual("user1@example.com", info.Email);
        Assert.AreEqual("true", info.EmailVerified);
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
