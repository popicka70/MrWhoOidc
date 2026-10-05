using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Infrastructure.Security;
using MrWhoOidc.WebAuth.Pages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Deactivated per-tenant users: deactivation only blocked the plain password path. The MFA branch of /Login, the
/// tenant ticket path, /LoginTotp and QR confirm still signed the user in, and existing sessions and tokens
/// outlived the deactivation.
/// </summary>
[TestClass]
public sealed class DeactivatedUserGateTests
{
    [TestMethod]
    public async Task Deactivate_RotatesAccountStamp_AndRevokesOnlyThatUsersTokens()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = new UserAccount { Username = "alice", PasswordHash = "h", SecurityStamp = "stamp-1" };
        var user = new User { TenantId = Guid.NewGuid(), Username = "alice", UserAccountId = account.Id };
        var other = new User { TenantId = user.TenantId, Username = "bob" };
        db.UserAccounts.Add(account);
        db.Users.AddRange(user, other);
        db.Tokens.AddRange(
            new Token { TenantId = user.TenantId, UserId = user.Id, ClientId = "c", Type = "refresh", TokenHash = "a", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
            new Token { TenantId = user.TenantId, UserId = other.Id, ClientId = "c", Type = "refresh", TokenHash = "b", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();

        var revoked = await ActiveUserGate.DeactivateAsync(db, user);
        await db.SaveChangesAsync();

        Assert.AreEqual(1, revoked);
        Assert.AreEqual(UserStatus.Deactivated, user.Status);
        Assert.IsNotNull(user.DeactivatedAt);
        Assert.AreNotEqual("stamp-1", db.UserAccounts.Single().SecurityStamp, "existing cookies must fail the stamp check");
        Assert.IsNotNull(db.Tokens.Single(t => t.UserId == user.Id).RevokedAt);
        Assert.IsNull(db.Tokens.Single(t => t.UserId == other.Id).RevokedAt);
    }

    [TestMethod]
    public async Task CookieValidator_RejectsSessionOfDeactivatedTenantUser_EvenWithCurrentStamp()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = new UserAccount { Username = "alice", PasswordHash = "h", SecurityStamp = "stamp-1" };
        var user = new User { TenantId = Guid.NewGuid(), Username = "alice", UserAccountId = account.Id, Status = UserStatus.Deactivated };
        db.UserAccounts.Add(account);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var ctx = CreateCookieContext(db,
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(UserClaimTypes.UserAccountId, account.Id.ToString()),
            new Claim(SecurityStampCookieValidator.SecurityStampClaimType, "stamp-1"));

        await SecurityStampCookieValidator.ValidateAsync(ctx);

        Assert.IsNull(ctx.Principal);
    }

    [TestMethod]
    public async Task Login_WhenMfaRequired_AndUserDeactivated_IssuesNoPreauthCookie()
    {
        var tenantId = Guid.NewGuid();
        var account = new UserAccount { Id = Guid.NewGuid(), Username = "alice", PasswordHash = "h", TotpEnabled = true, TotpSecret = "s" };
        var users = new Mock<IUserService>();
        users.Setup(s => s.FindByAccountIdAsync(account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { TenantId = tenantId, Username = "alice", UserAccountId = account.Id, Status = UserStatus.Deactivated });
        var globalAuth = new Mock<IGlobalAuthenticationService>();
        globalAuth.Setup(s => s.AuthenticateAsync("alice", "secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(GlobalAuthenticationResult.MfaRequired(account, [new UserTenantMembership { UserAccountId = account.Id, TenantId = tenantId, Status = TenantMembershipStatus.Active }]));
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.CurrentTenant).Returns(new TenantContext { TenantId = tenantId, Slug = "t", Name = "T", IssuerUri = "https://issuer/t" });
        var rateLimiter = new Mock<ILoginRateLimiter>();

        var model = new LoginModel(
            users.Object,
            globalAuth.Object,
            NullLogger<LoginModel>.Instance,
            tenantAccessor.Object,
            Mock.Of<IMultiTenancyOptions>(),
            Mock.Of<ITenantSettingsService>(),
            Mock.Of<ITenantBrandingService>(),
            Mock.Of<ITenantCredentialTicketStore>(),
            Mock.Of<ILoginContinuationStore>(),
            rateLimiter.Object,
            Mock.Of<IWebAuthnService>(),
            Options.Create(new WebAuthnOptions()));
        var (http, auth) = CreateHttpContext();
        AttachPageContext(model, http);
        model.Username = "alice";
        model.Password = "secret";

        var result = await model.OnPostAsync();

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.IsFalse(model.ModelState.IsValid);
        auth.Verify(a => a.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()), Times.Never);
    }

    [TestMethod]
    public async Task LoginTotp_WhenUserDeactivatedAfterPasswordStep_IsSentBackToLogin()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = new UserAccount { Username = "alice", PasswordHash = "h", TotpEnabled = true, TotpSecret = "secret" };
        var user = new User { TenantId = Guid.NewGuid(), Username = "alice", UserAccountId = account.Id, Status = UserStatus.Deactivated };
        db.UserAccounts.Add(account);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var totp = new Mock<ITotpService>();
        totp.Setup(t => t.FindMatchingStep(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>())).Returns(1000L);
        var accounts = new Mock<IUserAccountService>();
        accounts.Setup(a => a.FindForUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>())).ReturnsAsync(account);
        accounts.Setup(a => a.GetMfaStatusAsync(account.Id, It.IsAny<CancellationToken>())).ReturnsAsync((true, "secret"));
        var rateLimiter = new Mock<ILoginRateLimiter>();
        var model = new LoginTotpModel(db, new MfaCodeVerifier(db, totp.Object), accounts.Object, Mock.Of<IGlobalAuthenticationService>(), rateLimiter.Object, NullLogger<LoginTotpModel>.Instance)
        {
            Code = "123456"
        };
        var (http, auth) = CreateHttpContext();
        auth.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), "preauth"))
            .ReturnsAsync(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "preauth")), "preauth")));
        AttachPageContext(model, http);

        var result = await model.OnPostAsync();

        Assert.IsInstanceOfType<RedirectToPageResult>(result);
        auth.Verify(a => a.SignInAsync(It.IsAny<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()), Times.Never);
    }

    internal static (DefaultHttpContext Http, Mock<IAuthenticationService> Auth) CreateHttpContext()
    {
        var auth = new Mock<IAuthenticationService>();
        var services = new ServiceCollection().AddSingleton(auth.Object).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Scheme = "https";
        return (http, auth);
    }

    internal static void AttachPageContext(PageModel model, HttpContext http)
    {
        model.PageContext = new PageContext { HttpContext = http };
        var url = new Mock<IUrlHelper>();
        url.SetupGet(u => u.ActionContext).Returns(new ActionContext(http, new RouteData(), new ActionDescriptor()));
        url.Setup(u => u.RouteUrl(It.IsAny<UrlRouteContext>())).Returns("/LoginTotp");
        model.Url = url.Object;
        model.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
    }

    private static CookieValidatePrincipalContext CreateCookieContext(AuthDbContext db, params Claim[] claims)
    {
        var auth = new Mock<IAuthenticationService>();
        var services = new ServiceCollection().AddSingleton(db).AddSingleton(auth.Object).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return new CookieValidatePrincipalContext(
            http,
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(principal, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
