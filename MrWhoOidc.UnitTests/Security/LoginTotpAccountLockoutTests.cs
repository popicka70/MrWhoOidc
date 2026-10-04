using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Pages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// TOTP failures only fed the IP+username limiter, so they never counted towards the account lockout that password
/// failures trigger, and a locked-out account could still try codes.
/// </summary>
[TestClass]
public sealed class LoginTotpAccountLockoutTests
{
    private static async Task<(LoginTotpModel Model, Mock<IGlobalAuthenticationService> GlobalAuth, Mock<IAuthenticationService> Auth, UserAccount Account)> CreateAsync(bool codeValid, bool accountLocked)
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var account = new UserAccount { Username = "alice", PasswordHash = "h", TotpEnabled = true, TotpSecret = "secret" };
        var user = new User { TenantId = Guid.NewGuid(), Username = "alice", UserAccountId = account.Id };
        db.UserAccounts.Add(account);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var totp = new Mock<ITotpService>();
        totp.Setup(t => t.VerifyCode(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>())).Returns(codeValid);
        var accounts = new Mock<IUserAccountService>();
        accounts.Setup(a => a.FindForUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>())).ReturnsAsync(account);
        accounts.Setup(a => a.GetMfaStatusAsync(account.Id, It.IsAny<CancellationToken>())).ReturnsAsync((true, "secret"));
        var globalAuth = new Mock<IGlobalAuthenticationService>();
        globalAuth.Setup(g => g.IsLockedOutAsync(account.Id, It.IsAny<CancellationToken>())).ReturnsAsync(accountLocked);
        var model = new LoginTotpModel(db, totp.Object, accounts.Object, globalAuth.Object, Mock.Of<ILoginRateLimiter>(), NullLogger<LoginTotpModel>.Instance)
        {
            Code = "123456"
        };
        var (http, auth) = DeactivatedUserGateTests.CreateHttpContext();
        auth.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), "preauth"))
            .ReturnsAsync(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "preauth")), "preauth")));
        DeactivatedUserGateTests.AttachPageContext(model, http);
        return (model, globalAuth, auth, account);
    }

    [TestMethod]
    public async Task WrongCode_CountsTowardsAccountLockout()
    {
        var (model, globalAuth, _, account) = await CreateAsync(codeValid: false, accountLocked: false);

        var result = await model.OnPostAsync();

        Assert.IsInstanceOfType<PageResult>(result);
        globalAuth.Verify(g => g.RecordFailedAttemptAsync(account.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task LockedAccount_IsRefusedEvenWithAValidCode()
    {
        var (model, globalAuth, auth, account) = await CreateAsync(codeValid: true, accountLocked: true);

        var result = await model.OnPostAsync();

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.IsFalse(model.ModelState.IsValid);
        auth.Verify(a => a.SignInAsync(It.IsAny<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()), Times.Never);
        globalAuth.Verify(g => g.ClearFailedAttemptsAsync(account.Id, It.IsAny<CancellationToken>()), Times.Never);
    }
}
