using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Services;
using MfaIndexModel = MrWhoOidc.WebAuth.Pages.Mfa.IndexModel;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// H8 and the V4 chain of the third 2026-10-04 review: TOTP lives on the global account, but passkey, external,
/// device and CIBA sign-ins read the per-tenant flag; and /Mfa disabled TOTP for any session without a code.
/// </summary>
[TestClass]
public sealed class AccountMfaStateTests
{
    private static (User User, UserAccount Account) Seed(AuthDbContext db, bool accountTotp, bool userTotp)
    {
        var user = new User { TenantId = Guid.NewGuid(), Username = "alice", Email = "alice@example.com", NormalizedEmail = "alice@example.com", TotpEnabled = userTotp };
        var account = new UserAccount
        {
            Id = user.Id,
            Username = "alice",
            Email = "alice@example.com",
            NormalizedEmail = "alice@example.com",
            PasswordHash = "hash",
            TotpEnabled = accountTotp,
            TotpSecret = accountTotp ? "JBSWY3DPEHPK3PXP" : null,
        };
        user.UserAccountId = account.Id;
        db.Users.Add(user);
        db.UserAccounts.Add(account);
        db.SaveChanges();
        return (user, account);
    }

    private static DefaultHttpContext CreateHttpContext(AuthDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUserAccountService>(new UserAccountService(db, NullLogger<UserAccountService>.Instance));
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    [TestMethod]
    public async Task HasTotp_AccountEnrolledButTenantFlagOff_RequiresTotp()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var (user, _) = Seed(db, accountTotp: true, userTotp: false);

        Assert.IsTrue(await MfaState.HasTotpAsync(CreateHttpContext(db), user));
    }

    [TestMethod]
    public async Task HasTotp_StaleTenantFlagButAccountNotEnrolled_DoesNotRequireTotp()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var (user, _) = Seed(db, accountTotp: false, userTotp: true);

        Assert.IsFalse(await MfaState.HasTotpAsync(CreateHttpContext(db), user), "/LoginTotp verifies against the account and would bounce");
    }

    private static MfaIndexModel CreateMfaPage(AuthDbContext db, User user, Mock<ITotpService> totp)
    {
        var settings = new Mock<ITenantSettingsService>();
        settings.Setup(s => s.GetCurrentTenantSettingsAsync()).ReturnsAsync(new TenantSettings());
        var limiter = new Mock<ILoginRateLimiter>();

        var http = CreateHttpContext(db);
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Cookies"));
        return new MfaIndexModel(
            db,
            totp.Object,
            new MfaCodeVerifier(db, totp.Object),
            Mock.Of<IQrCodeGenerator>(),
            settings.Object,
            new UserAccountService(db, NullLogger<UserAccountService>.Instance),
            limiter.Object,
            NullLogger<MfaIndexModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, Mock.Of<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>()),
        };
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("000000")]
    public async Task DisableMfa_WithoutAValidCurrentCode_IsRefused(string? code)
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var (user, account) = Seed(db, accountTotp: true, userTotp: true);
        var totp = new Mock<ITotpService>();
        totp.Setup(t => t.FindMatchingStep(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>())).Returns((long?)null);
        var page = CreateMfaPage(db, user, totp);
        page.Action = "disable";
        page.VerificationCode = code;

        var result = await page.OnPostAsync();

        Assert.IsInstanceOfType<PageResult>(result);
        db.ChangeTracker.Clear();
        Assert.IsTrue(db.UserAccounts.Single(a => a.Id == account.Id).TotpEnabled, "TOTP must stay on without a valid code");
    }

    [TestMethod]
    public async Task DisableMfa_WithAValidCurrentCode_TurnsTotpOff()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var (user, account) = Seed(db, accountTotp: true, userTotp: true);
        var totp = new Mock<ITotpService>();
        totp.Setup(t => t.FindMatchingStep("JBSWY3DPEHPK3PXP", "123456", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>())).Returns(1000L);
        var page = CreateMfaPage(db, user, totp);
        page.Action = "disable";
        page.VerificationCode = "123456";

        await page.OnPostAsync();

        db.ChangeTracker.Clear();
        Assert.IsFalse(db.UserAccounts.Single(a => a.Id == account.Id).TotpEnabled);
    }
}
