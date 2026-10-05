using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Models.Delegation;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Pages.Account;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// 2026-10-04 assessment: adding a passkey and changing the email address needed nothing beyond a session, so a
/// stolen or left-open session could plant a lasting credential or take over account recovery. Both now require
/// the session's auth_time to be recent.
/// </summary>
[TestClass]
public sealed class RecentAuthenticationTests
{
    private static ClaimsPrincipal Principal(Guid userId, DateTimeOffset? authTime)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (authTime is { } t)
        {
            claims.Add(new Claim("auth_time", t.ToUnixTimeSeconds().ToString()));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    [TestMethod]
    public void IsRecent_DependsOnAuthTime()
    {
        var now = DateTimeOffset.UtcNow;
        var maxAge = TimeSpan.FromMinutes(10);

        Assert.IsTrue(RecentAuthentication.IsRecent(Principal(Guid.NewGuid(), now.AddMinutes(-2)), maxAge, now));
        Assert.IsFalse(RecentAuthentication.IsRecent(Principal(Guid.NewGuid(), now.AddMinutes(-11)), maxAge, now));
        Assert.IsFalse(RecentAuthentication.IsRecent(Principal(Guid.NewGuid(), null), maxAge, now), "no auth_time is not recent");
        Assert.IsFalse(RecentAuthentication.IsRecent(Principal(Guid.NewGuid(), now.AddHours(1)), maxAge, now), "a future auth_time is not trusted");
    }

    [TestMethod]
    public void IsRecent_UsesConfiguredMaxAge()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(Options.Create(new RecentAuthenticationOptions { MaxAge = TimeSpan.FromMinutes(1) }))
                .BuildServiceProvider(),
            User = Principal(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-5)),
        };

        Assert.IsFalse(RecentAuthentication.IsRecent(http));
    }

    private static WebAuthnHandler CreateWebAuthnHandler(AuthDbContext db)
        => new(
            Mock.Of<IWebAuthnService>(),
            Mock.Of<ITenantAccessor>(),
            db,
            NullLogger<WebAuthnHandler>.Instance,
            Mock.Of<IMultiTenancyOptions>(),
            Mock.Of<ITenantSettingsService>());

    private static DefaultHttpContext JsonContext(ClaimsPrincipal user)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            User = user,
        };
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, DefaultHttpContext http)
    {
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PasskeyRegistration_WithStaleSession_RequiresReauthentication(bool completion)
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var handler = CreateWebAuthnHandler(db);
        var http = JsonContext(Principal(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-2)));

        var result = completion ? await handler.RegistrationCompletionAsync(http) : await handler.RegistrationChallengeAsync(http);
        var (status, body) = await ExecuteAsync(result, http);

        Assert.AreEqual(StatusCodes.Status403Forbidden, status);
        StringAssert.Contains(body, "reauthentication_required");
        StringAssert.Contains(body, "\"login_url\":\"/login\"");
    }

    [TestMethod]
    public async Task PasskeyRegistration_WithRecentSession_PassesTheCheck()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var handler = CreateWebAuthnHandler(db);
        var http = JsonContext(Principal(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1)));

        var (status, body) = await ExecuteAsync(await handler.RegistrationChallengeAsync(http), http);

        // Gets as far as the tenant check (no tenant in this test).
        Assert.AreEqual(StatusCodes.Status400BadRequest, status, body);
    }

    private static async Task<(ProfileModel Page, User User)> CreateProfileAsync(AuthDbContext db, DateTimeOffset? authTime, string newEmail, string newName = "Alice")
    {
        var user = new User { TenantId = Guid.NewGuid(), Username = "alice", Name = "Alice", Email = "alice@example.com", NormalizedEmail = "ALICE@EXAMPLE.COM" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var accessor = new Mock<IEffectiveAccessContextAccessor>();
        accessor.Setup(a => a.GetContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveAccessContext(user.Id, user.Id, user.TenantId, AccessContextKind.Normal, null, null));
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
            User = Principal(user.Id, authTime),
        };
        var page = new ProfileModel(db, accessor.Object, Mock.Of<IUserAccountProvisioner>())
        {
            PageContext = new PageContext { HttpContext = http },
            Input = new ProfileModel.ProfileInput { Name = newName, Email = newEmail },
        };
        return (page, user);
    }

    [TestMethod]
    public async Task EmailChange_WithStaleSession_RedirectsToLoginAndKeepsTheEmail()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var (page, user) = await CreateProfileAsync(db, DateTimeOffset.UtcNow.AddHours(-1), "attacker@example.com");

        var result = await page.OnPostAsync();

        Assert.IsInstanceOfType<RedirectToPageResult>(result);
        Assert.AreEqual("/Login", ((RedirectToPageResult)result).PageName);
        db.ChangeTracker.Clear();
        Assert.AreEqual("alice@example.com", db.Users.Single(u => u.Id == user.Id).Email);
    }

    [TestMethod]
    public async Task EmailChange_WithRecentSession_IsSaved()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var (page, user) = await CreateProfileAsync(db, DateTimeOffset.UtcNow.AddMinutes(-1), "alice@new.example.com");

        await page.OnPostAsync();

        db.ChangeTracker.Clear();
        Assert.AreEqual("alice@new.example.com", db.Users.Single(u => u.Id == user.Id).Email);
    }

    [TestMethod]
    public async Task NameOnlyChange_DoesNotRequireReauthentication()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var (page, user) = await CreateProfileAsync(db, DateTimeOffset.UtcNow.AddHours(-1), "alice@example.com", newName: "Alice B");

        await page.OnPostAsync();

        db.ChangeTracker.Clear();
        Assert.AreEqual("Alice B", db.Users.Single(u => u.Id == user.Id).Name);
    }
}
