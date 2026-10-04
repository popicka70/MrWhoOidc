using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Pages;
using MrWhoOidc.WebAuth.Services;
using MfaIndexModel = MrWhoOidc.WebAuth.Pages.Mfa.IndexModel;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// 2026-10-04 assessment: there was no recovery path for a lost authenticator. Recovery codes are issued at MFA
/// confirmation, stored hashed, single-use, and accepted on /LoginTotp in place of a TOTP code.
/// </summary>
[TestClass]
public sealed class MfaRecoveryCodeTests
{
    private static readonly Regex DisplayFormat = new("^[A-Z2-7]{4}(-[A-Z2-7]{4}){3}$");

    [TestMethod]
    public async Task Regenerate_IssuesTenDistinctCodes_AndStoresOnlyHashes()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = TotpReplayTests.SeedAccount(db);
        var verifier = new MfaCodeVerifier(db, new TotpService());

        var codes = await verifier.RegenerateRecoveryCodesAsync(account.Id);

        Assert.AreEqual(10, codes.Count);
        Assert.AreEqual(10, codes.Distinct().Count());
        Assert.IsTrue(codes.All(c => DisplayFormat.IsMatch(c)), string.Join(",", codes));
        var stored = await db.UserAccountRecoveryCodes.Where(c => c.UserAccountId == account.Id).ToListAsync();
        Assert.AreEqual(10, stored.Count);
        foreach (var code in codes)
        {
            Assert.IsFalse(stored.Any(s => s.CodeHash.Contains(code.Replace("-", ""), StringComparison.OrdinalIgnoreCase)), "plaintext must not be stored");
        }
        Assert.AreEqual(10, await verifier.CountUnusedRecoveryCodesAsync(account.Id));
    }

    [TestMethod]
    public async Task RecoveryCode_IsSingleUse_InMemory()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = TotpReplayTests.SeedAccount(db);
        var verifier = new MfaCodeVerifier(db, new TotpService());
        var codes = await verifier.RegenerateRecoveryCodesAsync(account.Id);

        Assert.IsTrue(await verifier.ConsumeRecoveryCodeAsync(account.Id, codes[0].ToLowerInvariant().Replace("-", " ")), "normalised input must match");
        Assert.IsFalse(await verifier.ConsumeRecoveryCodeAsync(account.Id, codes[0]), "a used code must not work again");
        Assert.AreEqual(9, await verifier.CountUnusedRecoveryCodesAsync(account.Id));
    }

    [TestMethod]
    public async Task RecoveryCode_IsSingleUse_Relational()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var account = TotpReplayTests.SeedAccount(db);
        var codes = await new MfaCodeVerifier(db, new TotpService()).RegenerateRecoveryCodesAsync(account.Id);

        await using var db2 = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).Options);
        Assert.IsTrue(await new MfaCodeVerifier(db, new TotpService()).ConsumeRecoveryCodeAsync(account.Id, codes[3]));
        Assert.IsFalse(await new MfaCodeVerifier(db2, new TotpService()).ConsumeRecoveryCodeAsync(account.Id, codes[3]));
    }

    [TestMethod]
    public async Task RecoveryCode_DoesNotWorkForAnotherAccount_OrAfterRegeneration()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = TotpReplayTests.SeedAccount(db);
        var other = new UserAccount { Username = "mallory", PasswordHash = "h", TotpEnabled = true, TotpSecret = TotpReplayTests.Secret };
        db.UserAccounts.Add(other);
        await db.SaveChangesAsync();
        var verifier = new MfaCodeVerifier(db, new TotpService());
        var old = await verifier.RegenerateRecoveryCodesAsync(account.Id);

        Assert.IsFalse(await verifier.ConsumeRecoveryCodeAsync(other.Id, old[0]));
        await verifier.RegenerateRecoveryCodesAsync(account.Id);
        Assert.IsFalse(await verifier.ConsumeRecoveryCodeAsync(account.Id, old[1]), "regeneration must invalidate earlier codes");
    }

    [TestMethod]
    public async Task DisableMfa_RemovesRecoveryCodes()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = TotpReplayTests.SeedAccount(db);
        await new MfaCodeVerifier(db, new TotpService()).RegenerateRecoveryCodesAsync(account.Id);

        await new UserAccountService(db).DisableMfaAsync(account.Id);

        Assert.AreEqual(0, await db.UserAccountRecoveryCodes.CountAsync());
    }

    private static async Task<(LoginTotpModel Model, Mock<IAuthenticationService> Auth, Mock<IGlobalAuthenticationService> GlobalAuth, IReadOnlyList<string> Codes, Guid AccountId)> CreateLoginTotpAsync(AuthDbContext db)
    {
        var account = TotpReplayTests.SeedAccount(db);
        var user = new User { TenantId = Guid.NewGuid(), Username = "alice", UserAccountId = account.Id };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var verifier = new MfaCodeVerifier(db, new TotpService());
        var codes = await verifier.RegenerateRecoveryCodesAsync(account.Id);

        var accounts = new Mock<IUserAccountService>();
        accounts.Setup(a => a.FindForUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>())).ReturnsAsync(account);
        accounts.Setup(a => a.GetMfaStatusAsync(account.Id, It.IsAny<CancellationToken>())).ReturnsAsync((true, TotpReplayTests.Secret));
        var globalAuth = new Mock<IGlobalAuthenticationService>();
        var model = new LoginTotpModel(db, verifier, accounts.Object, globalAuth.Object, Mock.Of<ILoginRateLimiter>(), NullLogger<LoginTotpModel>.Instance);
        var (http, auth) = DeactivatedUserGateTests.CreateHttpContext();
        http.Features.Set<Microsoft.AspNetCore.Http.Features.ISessionFeature>(new SessionFeatureStub());
        auth.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), "preauth"))
            .ReturnsAsync(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "preauth")), "preauth")));
        DeactivatedUserGateTests.AttachPageContext(model, http);
        return (model, auth, globalAuth, codes, account.Id);
    }

    [TestMethod]
    public async Task LoginTotp_AcceptsARecoveryCodeOnce()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var (model, auth, _, codes, _) = await CreateLoginTotpAsync(db);
        model.Code = codes[0];

        await model.OnPostAsync();

        auth.Verify(a => a.SignInAsync(It.IsAny<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()), Times.Once);

        var result = await model.OnPostAsync();

        Assert.IsInstanceOfType<PageResult>(result, "the same recovery code must not sign in twice");
        auth.Verify(a => a.SignInAsync(It.IsAny<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()), Times.Once);
    }

    [TestMethod]
    public async Task LoginTotp_WrongRecoveryCode_CountsTowardsLockout()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var (model, auth, globalAuth, _, accountId) = await CreateLoginTotpAsync(db);
        model.Code = "AAAA-BBBB-CCCC-DDDD";

        var result = await model.OnPostAsync();

        Assert.IsInstanceOfType<PageResult>(result);
        globalAuth.Verify(g => g.RecordFailedAttemptAsync(accountId, It.IsAny<CancellationToken>()), Times.Once);
        auth.Verify(a => a.SignInAsync(It.IsAny<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()), Times.Never);
    }

    [TestMethod]
    public async Task MfaConfirm_ShowsRecoveryCodesOnce()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = new UserAccount { Username = "carol", PasswordHash = "h", TotpSecret = TotpReplayTests.Secret, TotpAlgorithm = "SHA1" };
        var user = new User { TenantId = Guid.NewGuid(), Username = "carol", UserAccountId = account.Id };
        db.UserAccounts.Add(account);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var page = CreateMfaPage(db, user);
        page.Action = "confirm";
        page.VerificationCode = TotpReplayTests.CurrentCode(algorithm: "SHA1");

        var result = await page.OnPostAsync();

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.AreEqual(10, page.RecoveryCodes?.Count);
        Assert.AreEqual(10, await db.UserAccountRecoveryCodes.CountAsync(c => c.UserAccountId == account.Id));
    }

    [TestMethod]
    public async Task RegenerateRecoveryCodes_RequiresAValidTotpCode()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var account = new UserAccount { Username = "dave", PasswordHash = "h", TotpEnabled = true, TotpSecret = TotpReplayTests.Secret, TotpAlgorithm = "SHA1" };
        var user = new User { TenantId = Guid.NewGuid(), Username = "dave", UserAccountId = account.Id };
        db.UserAccounts.Add(account);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var original = await new MfaCodeVerifier(db, new TotpService()).RegenerateRecoveryCodesAsync(account.Id);

        var refused = CreateMfaPage(db, user);
        refused.Action = "regenerate-recovery";
        refused.VerificationCode = "000000";
        await refused.OnPostAsync();
        Assert.IsNull(refused.RecoveryCodes);

        var accepted = CreateMfaPage(db, user);
        accepted.Action = "regenerate-recovery";
        accepted.VerificationCode = TotpReplayTests.CurrentCode(algorithm: "SHA1");
        await accepted.OnPostAsync();
        Assert.AreEqual(10, accepted.RecoveryCodes?.Count);
        Assert.IsFalse(await new MfaCodeVerifier(db, new TotpService()).ConsumeRecoveryCodeAsync(account.Id, original[0]));
    }

    private static MfaIndexModel CreateMfaPage(AuthDbContext db, User user)
    {
        var settings = new Mock<ITenantSettingsService>();
        settings.Setup(s => s.GetCurrentTenantSettingsAsync()).ReturnsAsync(new TenantSettings());
        var services = new ServiceCollection();
        services.AddSingleton(new MrWhoOidc.Auth.Options.OidcOptions { Issuer = "https://issuer.test" });
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Cookies"));
        var totp = new TotpService();
        return new MfaIndexModel(
            db,
            totp,
            new MfaCodeVerifier(db, totp),
            Mock.Of<IQrCodeGenerator>(),
            settings.Object,
            new UserAccountService(db, NullLogger<UserAccountService>.Instance),
            Mock.Of<ILoginRateLimiter>(),
            NullLogger<MfaIndexModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, Mock.Of<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>()),
        };
    }

    private sealed class SessionFeatureStub : Microsoft.AspNetCore.Http.Features.ISessionFeature
    {
        public ISession Session { get; set; } = new InMemorySession();
    }

    private sealed class InMemorySession : ISession
    {
        private readonly Dictionary<string, byte[]> _store = new();
        public bool IsAvailable => true;
        public string Id => "test";
        public IEnumerable<string> Keys => _store.Keys;
        public void Clear() => _store.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _store.Remove(key);
        public void Set(string key, byte[] value) => _store[key] = value;
        public bool TryGetValue(string key, out byte[] value) => _store.TryGetValue(key, out value!);
    }
}
