using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Handlers.External;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// C16 of the 2026-10-04 assessment: linking an external identity to an existing local account must
/// require proof of ownership and must not be completable from another browser.
/// </summary>
[TestClass]
public sealed class ExternalLinkConfirmationTests
{
    private const string Binding = "binding-value";

    private static (ExternalOidcHandler Handler, AuthDbContext Db, User Target) Create(ConfirmModel model)
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var target = new User { Id = model.TargetUserId, TenantId = Guid.NewGuid(), Username = "victim" };
        db.Users.Add(target);
        db.SaveChanges();

        var state = new Mock<IExternalOidcStateManager>();
        state.Setup(s => s.UnprotectConfirm("tok")).Returns(model);
        var session = new Mock<IExternalOidcSessionManager>();

        var handler = new ExternalOidcHandler(db, Mock.Of<IClaimMappingService>(), new TenantAccessor(), state.Object,
            Mock.Of<IExternalOidcCorrelationManager>(), Mock.Of<IExternalOidcDiscoveryService>(), Mock.Of<IExternalOidcRequestBuilder>(),
            Mock.Of<IExternalOidcTokenExchangeService>(), Mock.Of<IExternalOidcTokenValidator>(), Mock.Of<IExternalOidcUserProvisioner>(),
            session.Object, Mock.Of<IExternalOidcErrorHandler>(), Mock.Of<IExternalOidcMetricsRecorder>(), NullLogger<ExternalOidcHandler>.Instance);
        return (handler, db, target);
    }

    private static ConfirmModel Model() => new()
    {
        Provider = "google", Issuer = "https://accounts.example", Subject = "attacker-sub",
        TargetUserId = Guid.NewGuid(), BrowserBinding = Binding, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
    };

    private static HttpContext Http(string? bindingCookie, Guid? signedInUser)
    {
        var auth = new Mock<IAuthenticationService>();
        auth.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string?>()))
            .ReturnsAsync(signedInUser is { } uid
                ? AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, uid.ToString()) }, "cookie")), "Cookies"))
                : AuthenticateResult.NoResult());
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(auth.Object).BuildServiceProvider() };
        ctx.Request.Path = "/auth/external/confirm";
        ctx.Request.QueryString = new QueryString("?t=tok");
        if (bindingCookie is not null) ctx.Request.Headers.Cookie = $"__Host-mrwho-link={bindingCookie}";
        return ctx;
    }

    [TestMethod]
    public async Task Confirm_FromDifferentBrowser_IsRejected()
    {
        var model = Model();
        var (handler, db, _) = Create(model);

        var result = await handler.ConfirmLinkAsync(Http(bindingCookie: null, signedInUser: model.TargetUserId));

        Assert.IsInstanceOfType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>(result);
        Assert.AreEqual(0, db.ExternalIdentities.Count());
    }

    [TestMethod]
    public async Task Confirm_WithoutLocalLogin_RedirectsToLogin_AndDoesNotLink()
    {
        var model = Model();
        var (handler, db, _) = Create(model);

        var result = await handler.ConfirmLinkAsync(Http(Binding, signedInUser: null));

        var redirect = (Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult)result;
        StringAssert.StartsWith(redirect.Url, "/Login?ReturnUrl=");
        Assert.AreEqual(0, db.ExternalIdentities.Count());
    }

    [TestMethod]
    public async Task Confirm_SignedInAsTarget_Links()
    {
        var model = Model();
        var (handler, db, target) = Create(model);

        await handler.ConfirmLinkAsync(Http(Binding, signedInUser: target.Id));

        Assert.AreEqual(target.Id, db.ExternalIdentities.Single().UserId);
    }
}
