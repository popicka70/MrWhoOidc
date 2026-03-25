using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Pages.Logout.Prompt;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public class LogoutOpenRedirectTests
{
    private (IndexModel model, Mock<IUrlHelper> urlHelperMock) CreateModel()
    {
        var dbOptions = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AuthDbContext(dbOptions);

        var upstreamMock = new Mock<IUpstreamLogoutService>();
        var keyStoreMock = new Mock<IKeyStore>();
        var metrics = new OidcEndpointMetrics();
        var auditMock = new Mock<IAuditSink>();
        var logger = NullLogger<IndexModel>.Instance;
        var fedOpts = Options.Create(new FederatedLogoutOptions());

        var model = new IndexModel(upstreamMock.Object, db, keyStoreMock.Object, metrics, auditMock.Object, logger, fedOpts);

        var httpContext = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService, NoopAuthenticationService>();
        httpContext.RequestServices = services.BuildServiceProvider();
        var modelState = new ModelStateDictionary();
        var actionContext = new ActionContext(httpContext, new RouteData(), new PageActionDescriptor(), modelState);
        var modelMetadataProvider = new EmptyModelMetadataProvider();
        var viewData = new ViewDataDictionary(modelMetadataProvider, modelState);
        var tempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

        model.PageContext = new PageContext(actionContext)
        {
            ViewData = viewData
        };
        model.TempData = tempData;
        model.Url = Mock.Of<IUrlHelper>();

        var urlHelperMock = Mock.Get(model.Url);

        return (model, urlHelperMock);
    }

    private sealed class NoopAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    [TestMethod]
    public void OnGet_WithProtocolRelativeUrl_ShouldNotAcceptIt()
    {
        var (model, urlHelperMock) = CreateModel();
        string evilUrl = "//evil.com";

        urlHelperMock.Setup(x => x.IsLocalUrl(evilUrl)).Returns(false);

        model.OnGet(null, evilUrl, null, null, null);

        Assert.AreNotEqual(evilUrl, model.ReturnUrl, "OnGet should not accept protocol-relative URLs as ReturnUrl.");
    }

    [TestMethod]
    public async Task OnPostAsync_WithProtocolRelativeUrl_ShouldRedirectToRoot()
    {
        var (model, urlHelperMock) = CreateModel();
        string evilUrl = "//evil.com";

        urlHelperMock.Setup(x => x.IsLocalUrl(evilUrl)).Returns(false);

        var result = await model.OnPostAsync("local", evilUrl, null, null, null);

        if (result is RedirectResult redirectResult)
        {
            Assert.AreNotEqual(evilUrl, redirectResult.Url, "OnPostAsync should not redirect to protocol-relative URLs.");
        }
        else if (result is LocalRedirectResult localRedirectResult)
        {
            Assert.AreNotEqual(evilUrl, localRedirectResult.Url, "OnPostAsync should not local-redirect to protocol-relative URLs.");
        }
        else if (result is RedirectToPageResult redirectToPageResult)
        {
             // also fine if it redirects to root page
        }
    }
}
