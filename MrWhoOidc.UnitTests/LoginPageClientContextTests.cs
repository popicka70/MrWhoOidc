using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Pages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// The login page names the application that started the sign-in, so a user who did not expect the request sees
/// it before typing their password. The name comes from the client context of the authorize request, which the
/// resolver validates (a request object is verified, a pushed request is looked up).
/// </summary>
[TestClass]
public sealed class LoginPageClientContextTests
{
    [TestMethod]
    public async Task ThePage_NamesTheClientThatStartedTheSignIn()
    {
        var clientContext = new Mock<IReturnUrlClientContextResolver>();
        clientContext
            .Setup(r => r.TryResolveClientAsync(It.IsAny<HttpContext>(), "/authorize?client_id=demo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MrWhoOidc.Auth.Persistence.Client { ClientId = "demo", ClientName = "Demo Application" });

        var model = CreateModel(clientContext.Object);
        model.ReturnUrl = "/authorize?client_id=demo";

        await model.OnGetAsync();

        Assert.AreEqual("Demo Application", model.ClientName);
    }

    [TestMethod]
    public async Task ThePage_FallsBackToTheClientId_WhenTheClientHasNoName()
    {
        var clientContext = new Mock<IReturnUrlClientContextResolver>();
        clientContext
            .Setup(r => r.TryResolveClientAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MrWhoOidc.Auth.Persistence.Client { ClientId = "demo" });

        var model = CreateModel(clientContext.Object);
        model.ReturnUrl = "/authorize?client_id=demo";

        await model.OnGetAsync();

        Assert.AreEqual("demo", model.ClientName);
    }

    [TestMethod]
    public async Task ThePage_ShowsNoClient_WhenTheResolverFindsNone()
    {
        var clientContext = new Mock<IReturnUrlClientContextResolver>();
        clientContext
            .Setup(r => r.TryResolveClientAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client?)null);

        var model = CreateModel(clientContext.Object);
        model.ReturnUrl = "/authorize?scope=openid";

        await model.OnGetAsync();

        Assert.IsNull(model.ClientName);
    }

    [TestMethod]
    public async Task ThePage_StillSignsIn_WhenTheResolverFails()
    {
        var clientContext = new Mock<IReturnUrlClientContextResolver>();
        clientContext
            .Setup(r => r.TryResolveClientAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the key ring is gone"));

        var model = CreateModel(clientContext.Object);
        model.ReturnUrl = "/authorize?client_id=demo";

        await model.OnGetAsync();

        Assert.IsNull(model.ClientName);
    }

    private sealed class StubMultiTenancyOptions : IMultiTenancyOptions
    {
        public bool Enabled { get; init; }
        public string DefaultTenantSlug { get; init; } = "default";
    }

    private static LoginModel CreateModel(IReturnUrlClientContextResolver clientContextResolver)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.CurrentTenant).Returns(new TenantContext { TenantId = Guid.NewGuid(), Slug = "default" });

        var branding = new Mock<ITenantBrandingService>();
        branding.Setup(s => s.GetCurrentTenantBrandingAsync()).ReturnsAsync(new TenantBranding { TenantName = "Default" });

        var settingsService = new Mock<ITenantSettingsService>();
        settingsService.Setup(s => s.GetCurrentTenantSettingsAsync()).ReturnsAsync(new TenantSettings());

        var model = new LoginModel(
            Mock.Of<IUserService>(),
            Mock.Of<IGlobalAuthenticationService>(),
            NullLogger<LoginModel>.Instance,
            tenantAccessor.Object,
            new StubMultiTenancyOptions { Enabled = false, DefaultTenantSlug = "default" },
            settingsService.Object,
            branding.Object,
            Mock.Of<ITenantCredentialTicketStore>(),
            Mock.Of<ILoginContinuationStore>(),
            clientContextResolver,
            Mock.Of<ILoginRateLimiter>(),
            Mock.Of<IWebAuthnService>(),
            Options.Create(new WebAuthnOptions()))
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

        return model;
    }
}
