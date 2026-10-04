using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Handlers.External;
using MrWhoOidc.WebAuth.Infrastructure.Security;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Open-redirect defences (2026-10-04 assessment): user-supplied return URLs must be same-origin paths.
/// </summary>
[TestClass]
public sealed class SafeRedirectTests
{
    public static IEnumerable<object[]> UnsafeUrls =>
    [
        ["//evil.com"],
        ["/\\evil.com"],
        ["/foo\\..\\\\evil.com"],
        ["\\\\evil.com"],
        ["evil.com"],
        ["https://evil.com/"],
        ["javascript:alert(1)"],
        ["/\t/evil.com"],
        ["/\r\n/evil.com"],
        ["~/foo"],
        [""],
    ];

    [TestMethod]
    [DynamicData(nameof(UnsafeUrls))]
    public void IsSafeLocalPath_RejectsNonLocal(string url)
    {
        Assert.IsFalse(SafeRedirect.IsSafeLocalPath(url));
        Assert.AreEqual("/", SafeRedirect.LocalOrDefault(url));
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/authorize?client_id=web&prompt=login")]
    [DataRow("/t/acme/account#section")]
    public void IsSafeLocalPath_AcceptsLocalPaths(string url)
    {
        Assert.IsTrue(SafeRedirect.IsSafeLocalPath(url));
        Assert.AreEqual(url, SafeRedirect.LocalOrDefault(url));
    }

    [TestMethod]
    [DataRow("https://evil.com/authorize?prompt=login")]
    [DataRow("/\\evil.com/authorize?prompt=login")]
    [DataRow("//evil.com")]
    public void ConsumePromptValues_DoesNotPassNonLocalUrlsThrough(string url)
    {
        Assert.IsNull(AuthorizeReturnUrlHelper.ConsumePromptValues(url, "login", "select_account"));
    }

    [TestMethod]
    public async Task ExternalStart_WithNonLocalReturnUrl_IsRejected()
    {
        var (scope, handler, ctx) = ExternalOidcTestHost.Create();
        using (scope)
        {
            ctx.Request.QueryString = new QueryString("?provider=google&returnUrl=https%3A%2F%2Fevil.com%2F&clientId=web");

            var result = await handler.StartAsync(ctx);

            var redirect = (RedirectHttpResult)result;
            StringAssert.StartsWith(redirect.Url, "/auth/external/error");
            StringAssert.Contains(redirect.Url, "code=invalid_return_url");
            Assert.IsFalse(redirect.Url.Contains("evil.com", StringComparison.OrdinalIgnoreCase), redirect.Url);
        }
    }

    [TestMethod]
    [DataRow("https://evil.com/")]
    [DataRow("/\\evil.com")]
    public async Task ExternalConfirmLink_NeverRedirectsOffSite(string returnUrl)
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var model = new ConfirmModel
        {
            Provider = "google", Issuer = "https://accounts.example", Subject = "sub-1", TargetUserId = Guid.NewGuid(),
            ReturnUrl = returnUrl, BrowserBinding = "b", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        db.ExternalIdentities.Add(new ExternalIdentity { Issuer = model.Issuer, Subject = model.Subject, UserId = model.TargetUserId, ProviderName = "google" });
        db.SaveChanges();

        var state = new Mock<IExternalOidcStateManager>();
        state.Setup(s => s.UnprotectConfirm("tok")).Returns(model);
        var handler = new ExternalOidcHandler(db, Mock.Of<IClaimMappingService>(), new TenantAccessor(), state.Object,
            Mock.Of<IExternalOidcCorrelationManager>(), Mock.Of<IExternalOidcDiscoveryService>(), Mock.Of<IExternalOidcRequestBuilder>(),
            Mock.Of<IExternalOidcTokenExchangeService>(), Mock.Of<IExternalOidcTokenValidator>(), Mock.Of<IExternalOidcUserProvisioner>(),
            Mock.Of<IExternalOidcSessionManager>(), Mock.Of<IExternalOidcErrorHandler>(), Mock.Of<IExternalOidcMetricsRecorder>(), NullLogger<ExternalOidcHandler>.Instance);

        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(Mock.Of<IAuthenticationService>()).BuildServiceProvider() };
        ctx.Request.QueryString = new QueryString("?t=tok");
        ctx.Request.Headers.Cookie = "__Host-mrwho-link=b";

        var result = await handler.ConfirmLinkAsync(ctx);

        Assert.AreEqual("/", ((RedirectHttpResult)result).Url);
    }
}
