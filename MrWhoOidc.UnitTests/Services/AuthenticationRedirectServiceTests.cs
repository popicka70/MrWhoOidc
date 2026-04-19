using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Services;

[TestClass]
public sealed class AuthenticationRedirectServiceTests
{
    [TestMethod]
    public async Task RedirectToLoginAsync_When_LocalLogin_DisplayPopup_AddsDisplayQueryToLoginUrl()
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(x => x.CurrentTenant).Returns((TenantContext?)null);

        var continuationStore = new Mock<ILoginContinuationStore>();
        continuationStore
            .Setup(x => x.StoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ctx-1");

        var responseGen = new Mock<IAuthorizeResponseGenerator>();

        var svc = new AuthenticationRedirectService(tenantAccessor.Object, continuationStore.Object, responseGen.Object);

        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        http.Request.Path = "/authorize";
        http.Request.QueryString = new QueryString("?client_id=c1&display=popup");

        var selection = new ProviderSelectionResult(RequiresSelection: false, AutoRedirectProvider: null, AvailableProviders: null, AllowLocal: true);
        var validation = new AuthorizeValidationResult(IsValid: true, ClientId: "c1", RedirectUri: "https://cb");

        var result = await svc.RedirectToLoginAsync(http, selection, validation, display: "popup", ct: CancellationToken.None);
        var redirect = result as RedirectHttpResult;
        Assert.IsNotNull(redirect, "Expected redirect result");

        var location = redirect!.Url;
        Assert.IsTrue(location.StartsWith("/login?", StringComparison.Ordinal), "Expected /login redirect");
        Assert.IsTrue(location.Contains("ctx=ctx-1", StringComparison.Ordinal), "Expected ctx parameter");
        Assert.IsTrue(location.Contains("display=popup", StringComparison.Ordinal), "Expected display=popup parameter");
    }

    [TestMethod]
    public async Task RedirectToLoginAsync_UsesStoredLocalAuthorizeReturnUrl_ForContinuation()
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(x => x.CurrentTenant).Returns((TenantContext?)null);

        string? storedReturnUrl = null;
        var continuationStore = new Mock<ILoginContinuationStore>();
        continuationStore
            .Setup(x => x.StoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((returnUrl, _) => storedReturnUrl = returnUrl)
            .ReturnsAsync("ctx-2");

        var responseGen = new Mock<IAuthorizeResponseGenerator>();
        var svc = new AuthenticationRedirectService(tenantAccessor.Object, continuationStore.Object, responseGen.Object);

        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        http.Request.Path = "/authorize";
        http.Request.QueryString = new QueryString("?client_id=query-client");
        AuthorizeReturnUrlHelper.SetLocalAuthorizeReturnUrl(http, "/authorize?client_id=form-client&prompt=login");

        var selection = new ProviderSelectionResult(RequiresSelection: false, AutoRedirectProvider: null, AvailableProviders: null, AllowLocal: true);
        var validation = new AuthorizeValidationResult(IsValid: true, ClientId: "c1", RedirectUri: "https://cb");

        await svc.RedirectToLoginAsync(http, selection, validation, ct: CancellationToken.None);

        Assert.AreEqual("/authorize?client_id=form-client&prompt=login", storedReturnUrl);
    }

    [TestMethod]
    public void ConsumePromptValues_RemovesConsumedPrompts_FromAuthorizeReturnUrl()
    {
        var sanitized = AuthorizeReturnUrlHelper.ConsumePromptValues(
            "/authorize?client_id=c1&prompt=login%20consent&state=s1",
            "login");

        Assert.IsNotNull(sanitized);
        Assert.IsFalse(sanitized.Contains("login", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(sanitized, "prompt=consent", StringComparison.Ordinal);
        StringAssert.Contains(sanitized, "state=s1", StringComparison.Ordinal);
    }
}
