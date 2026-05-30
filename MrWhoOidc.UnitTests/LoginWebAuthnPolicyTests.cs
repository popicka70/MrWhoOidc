using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Pages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class LoginWebAuthnPolicyTests
{
    private sealed class StubTenantAccessor : ITenantAccessor
    {
        public TenantContext? CurrentTenant { get; private set; }

        public void SetTenant(TenantContext context)
        {
            CurrentTenant = context;
        }
    }

    private sealed class StubMultiTenancyOptions : IMultiTenancyOptions
    {
        public bool Enabled { get; init; }
        public string DefaultTenantSlug { get; init; } = "default";
    }

    [TestMethod]
    public async Task OnPostAsync_RedirectsToWebAuthn_WhenPolicyRequiresPasskeyAndCredentialExists()
    {
        var tenantId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var tenantAccessor = new StubTenantAccessor();
        tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = tenantId,
            Slug = "default",
            Name = "Default",
            IssuerUri = "https://issuer/default",
            IsMultiTenantMode = false
        });

        var users = new Mock<IUserService>();
        users.Setup(s => s.FindByUsernameAsync("alice", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                Id = userId,
                TenantId = tenantId,
                Username = "alice",
                TotpEnabled = false
            });

        var globalAuth = new Mock<IGlobalAuthenticationService>();
        globalAuth.Setup(s => s.AuthenticateAsync("alice", "secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(GlobalAuthenticationResult.Success(
                new UserAccount
                {
                    Id = accountId,
                    Username = "alice",
                    PasswordHash = "hash"
                },
                new[]
                {
                    new UserTenantMembership
                    {
                        Id = Guid.NewGuid(),
                        UserAccountId = accountId,
                        TenantId = tenantId,
                        Status = TenantMembershipStatus.Active
                    }
                }));

        var settingsService = new Mock<ITenantSettingsService>();
        settingsService.Setup(s => s.GetCurrentTenantSettingsAsync())
            .ReturnsAsync(new TenantSettings
            {
                Auth = new AuthTenantSettings { RequireMfa = false }
            });

        var branding = new Mock<ITenantBrandingService>();
        branding.Setup(s => s.GetCurrentTenantBrandingAsync())
            .ReturnsAsync(new TenantBranding { TenantName = "Default" });

        var ticketStore = new Mock<ITenantCredentialTicketStore>();
        var continuationStore = new Mock<ILoginContinuationStore>();
        var loginRateLimiter = new Mock<ILoginRateLimiter>();
        loginRateLimiter.Setup(l => l.IsLockedOutAsync(It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var webAuthn = new Mock<IWebAuthnService>();
        webAuthn.Setup(s => s.HasWebAuthnCredentialsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var model = new LoginModel(
            users.Object,
            globalAuth.Object,
            NullLogger<LoginModel>.Instance,
            tenantAccessor,
            new StubMultiTenancyOptions { Enabled = false, DefaultTenantSlug = "default" },
            settingsService.Object,
            branding.Object,
            ticketStore.Object,
            continuationStore.Object,
            loginRateLimiter.Object,
            webAuthn.Object,
            Options.Create(new WebAuthnOptions
            {
                Enabled = true,
                RequireWebAuthnForRegisteredUsers = true
            }));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";

        model.PageContext = new PageContext
        {
            HttpContext = httpContext
        };

        var urlHelper = new Mock<IUrlHelper>();
        urlHelper.SetupGet(u => u.ActionContext).Returns(new ActionContext(httpContext, new RouteData(), new ActionDescriptor()));
        urlHelper.Setup(u => u.RouteUrl(It.IsAny<UrlRouteContext>())).Returns("/Auth/WebAuthn?username=alice");
        model.Url = urlHelper.Object;

        model.Username = "alice";
        model.Password = "secret";
        model.ReturnUrl = "/";

        var result = await model.OnPostAsync();

        Assert.IsInstanceOfType<RedirectResult>(result);
        var redirect = (RedirectResult)result;
        Assert.IsTrue(redirect.Url!.Contains("/Auth/WebAuthn", StringComparison.Ordinal));
        webAuthn.Verify(s => s.HasWebAuthnCredentialsAsync(userId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
