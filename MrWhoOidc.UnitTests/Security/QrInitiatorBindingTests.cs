using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Infrastructure.Security;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Pages.Auth;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// H9 of the 2026-10-04 assessment: QR login was not bound to the browser that started it. Anyone holding the session
/// token (it is in the QR code, so an attacker can phish a victim with it) could poll the status for the authorization
/// code or complete the platform sign-in; the confirm step had no number matching; and /auth/qr rendered ?qr= as the image.
/// </summary>
[TestClass]
public sealed class QrInitiatorBindingTests
{
    private const string SessionToken = "qr-session-token";
    private const string InitiatorSecret = "initiator-secret-value";
    private const string MatchCode = "42";
    private const string MobileUrl = "https://idp.example/auth/qr-mobile?session=qr-session-token";

    private static QrLoginSession NewSession(QrSessionStatus status, string returnUrl = "https://rp.example/callback", Guid? userId = null) => new()
    {
        SessionToken = SessionToken,
        SessionTokenHash = CryptoHelper.ComputeSha256Hex(SessionToken),
        ClientId = "app",
        ReturnUrl = returnUrl,
        CodeChallenge = "challenge",
        Scope = "openid profile",
        State = "st",
        Status = status,
        UserId = userId,
        AuthorizationCode = status == QrSessionStatus.Authenticated && returnUrl.StartsWith("http") ? "the-code" : null,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
        InitiatorSecretHash = CryptoHelper.ComputeSha256Hex(InitiatorSecret),
        MatchCode = MatchCode,
    };

    private static (QrLoginHandler Handler, Mock<IQrLoginService> Qr, Mock<IAuthorizationCodeService> Codes) CreateHandler(QrLoginSession session, AuthDbContext? db = null)
    {
        var qr = new Mock<IQrLoginService>();
        qr.Setup(q => q.GetSessionAsync(SessionToken)).ReturnsAsync(session);
        qr.Setup(q => q.UpdateStatusAsync(SessionToken, It.IsAny<QrSessionStatus>(), It.IsAny<Guid?>(), It.IsAny<string?>())).ReturnsAsync(true);

        var assignments = new Mock<IUserClientAssignmentService>();
        assignments.Setup(a => a.EnsureAssignedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, null));
        var consent = new Mock<IConsentProcessor>();
        consent.Setup(c => c.EvaluateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsentDecision(RequiresConsent: false, HasConsent: true));

        var codes = new Mock<IAuthorizationCodeService>();
        codes.Setup(c => c.IssueAsync(It.IsAny<AuthorizeValidationResult>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>()))
            .ReturnsAsync((true, null, null, "issued-code"));

        var handler = new QrLoginHandler(
            qr.Object,
            Mock.Of<IQrCodeGenerator>(),
            codes.Object,
            db ?? TestDataSeeder.CreateInMemoryDb(),
            NullLogger<QrLoginHandler>.Instance,
            new NoopAuditSink(),
            Options.Create(new QrLoginOptions { Enabled = true }),
            Mock.Of<ILoginContinuationStore>(),
            assignments.Object,
            consent.Object);
        return (handler, qr, codes);
    }

    private static DefaultHttpContext Request(string? initiatorSecret)
    {
        var http = new DefaultHttpContext();
        if (initiatorSecret is not null)
        {
            http.Request.Headers.Cookie = $"{QrInitiatorBinding.CookieName(SessionToken)}={initiatorSecret}";
        }
        return http;
    }

    private static DefaultHttpContext ConfirmRequest(string? matchCode, Guid? userId = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.ContentType = "application/x-www-form-urlencoded";
        var form = new Dictionary<string, StringValues> { ["sessionToken"] = SessionToken };
        if (matchCode is not null) form["matchCode"] = matchCode;
        http.Request.Form = new FormCollection(form);
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString())], "Cookies"));
        return TestAntiforgeryHelper.ProtectPost(WithServices(http));
    }

    // ---- status polling -------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(null, DisplayName = "no initiator cookie")]
    [DataRow("someone-elses-secret", DisplayName = "wrong initiator cookie")]
    public async Task Status_WithoutTheInitiatorCookie_IsRefusedAndLeaksNoCode(string? cookie)
    {
        var (handler, _, _) = CreateHandler(NewSession(QrSessionStatus.Authenticated));
        var http = Request(cookie);
        http.Response.Body = new MemoryStream();

        var result = await handler.GetStatusAsync(http, SessionToken);

        Assert.AreEqual(403, (result as IStatusCodeHttpResult)?.StatusCode);
        await result.ExecuteAsync(WithServices(http));
        var body = ReadBody(http);
        Assert.IsFalse(body.Contains("the-code", StringComparison.Ordinal), body);
    }

    [TestMethod]
    public async Task Status_FromTheInitiatingBrowser_ReturnsTheRedirect()
    {
        var (handler, _, _) = CreateHandler(NewSession(QrSessionStatus.Authenticated));
        var http = Request(InitiatorSecret);
        http.Response.Body = new MemoryStream();

        var result = await handler.GetStatusAsync(http, SessionToken);
        await result.ExecuteAsync(WithServices(http));

        Assert.AreEqual(200, http.Response.StatusCode);
        StringAssert.Contains(ReadBody(http), "code=the-code");
    }

    [TestMethod]
    public async Task Status_ForASessionWithoutStoredBinding_IsRefused()
    {
        var legacy = NewSession(QrSessionStatus.Authenticated);
        legacy.InitiatorSecretHash = null;
        var (handler, _, _) = CreateHandler(legacy);

        var result = await handler.GetStatusAsync(Request(InitiatorSecret), SessionToken);

        Assert.AreEqual(403, (result as IStatusCodeHttpResult)?.StatusCode);
    }

    // ---- platform completion --------------------------------------------------------------------------------

    [TestMethod]
    public async Task Complete_WithoutTheInitiatorCookie_DoesNotSignIn()
    {
        var (handler, qr, _) = CreateHandler(NewSession(QrSessionStatus.Authenticated, returnUrl: "/", userId: Guid.NewGuid()));
        var auth = new Mock<IAuthenticationService>();
        var http = TestAntiforgeryHelper.ProtectPost(WithServices(Request("attacker-guess"), auth.Object));

        var result = await handler.CompleteAsync(http, SessionToken);

        Assert.AreEqual("/DiscoverTenant?error=browser_mismatch", (result as Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult)?.Url);
        auth.Verify(a => a.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()), Times.Never);
        qr.Verify(q => q.UpdateStatusAsync(SessionToken, QrSessionStatus.Consumed, It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    [TestMethod]
    public async Task Complete_FromTheInitiatingBrowser_SignsIn()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var user = new User { Username = "alice", TenantId = Guid.NewGuid() };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var (handler, qr, _) = CreateHandler(NewSession(QrSessionStatus.Authenticated, returnUrl: "/", userId: user.Id), db);
        var auth = new Mock<IAuthenticationService>();
        var http = TestAntiforgeryHelper.ProtectPost(WithServices(Request(InitiatorSecret), auth.Object));

        await handler.CompleteAsync(http, SessionToken);

        auth.Verify(a => a.SignInAsync(http, It.IsAny<string?>(), It.Is<ClaimsPrincipal>(p => p.FindFirst(ClaimTypes.NameIdentifier)!.Value == user.Id.ToString()), It.IsAny<AuthenticationProperties?>()), Times.Once);
        qr.Verify(q => q.UpdateStatusAsync(SessionToken, QrSessionStatus.Consumed, user.Id, It.IsAny<string?>()), Times.Once);
    }

    // ---- number matching on confirm -------------------------------------------------------------------------

    [TestMethod]
    public async Task Complete_GetWithInitiatorCookie_DoesNotSignIn()
    {
        var (handler, qr, _) = CreateHandler(NewSession(QrSessionStatus.Authenticated, "/", Guid.NewGuid()));
        var auth = new Mock<IAuthenticationService>();
        var http = WithServices(Request(InitiatorSecret), auth.Object);
        http.Request.Method = "GET";

        var result = await handler.CompleteAsync(http, SessionToken);

        Assert.AreEqual(405, (result as IStatusCodeHttpResult)?.StatusCode);
        Assert.AreEqual("POST", http.Response.Headers.Allow.ToString());
        auth.Verify(a => a.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()), Times.Never);
        qr.Verify(q => q.GetSessionAsync(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    [DataRow("complete", null)]
    [DataRow("complete", "forged")]
    [DataRow("confirm", null)]
    [DataRow("confirm", "forged")]
    [DataRow("cancel", null)]
    [DataRow("cancel", "forged")]
    public async Task QrWrites_WithMissingOrForgedAntiforgeryToken_HaveNoSideEffects(string operation, string? token)
    {
        var (handler, qr, codes) = CreateHandler(NewSession(QrSessionStatus.Authenticated, "/", Guid.NewGuid()));
        var auth = new Mock<IAuthenticationService>();
        var http = TestAntiforgeryHelper.ProtectPost(WithServices(Request(InitiatorSecret), auth.Object));
        var form = new Dictionary<string, StringValues> { ["sessionToken"] = SessionToken, ["matchCode"] = MatchCode };
        if (token is not null) form["__RequestVerificationToken"] = token;
        http.Request.Form = new FormCollection(form);

        var result = operation switch
        {
            "complete" => await handler.CompleteAsync(http, SessionToken),
            "confirm" => await handler.ConfirmAsync(http),
            _ => await handler.CancelAsync(http)
        };

        Assert.AreEqual(400, (result as IStatusCodeHttpResult)?.StatusCode);
        qr.Verify(q => q.GetSessionAsync(It.IsAny<string>()), Times.Never);
        qr.Verify(q => q.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<QrSessionStatus>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
        codes.Verify(c => c.IssueAsync(It.IsAny<AuthorizeValidationResult>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>()), Times.Never);
        auth.Verify(a => a.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()), Times.Never);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("wrong-secret")]
    [DataRow(InitiatorSecret)]
    public async Task Cancel_RequiresInitiatorBinding_EvenWithValidAntiforgery(string? secret)
    {
        var (handler, qr, _) = CreateHandler(NewSession(QrSessionStatus.Pending));
        var http = TestAntiforgeryHelper.ProtectPost(WithServices(Request(secret)));
        var form = http.Request.Form.ToDictionary(kv => kv.Key, kv => kv.Value);
        form["sessionToken"] = SessionToken;
        http.Request.Form = new FormCollection(form);

        var result = await handler.CancelAsync(http);

        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        Assert.AreEqual(secret == InitiatorSecret ? 200 : 403, http.Response.StatusCode);
        qr.Verify(q => q.UpdateStatusAsync(SessionToken, QrSessionStatus.Cancelled, It.IsAny<Guid?>(), It.IsAny<string?>()),
            secret == InitiatorSecret ? Times.Once() : Times.Never());
    }

    [TestMethod]
    public async Task Complete_ExpiredAuthenticatedSession_DoesNotSignIn()
    {
        var session = NewSession(QrSessionStatus.Authenticated, "/", Guid.NewGuid());
        session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var (handler, qr, _) = CreateHandler(session);
        var auth = new Mock<IAuthenticationService>();
        var http = TestAntiforgeryHelper.ProtectPost(WithServices(Request(InitiatorSecret), auth.Object));

        var result = await handler.CompleteAsync(http, SessionToken);

        Assert.AreEqual("/DiscoverTenant?error=not_authenticated", (result as Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult)?.Url);
        auth.Verify(a => a.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()), Times.Never);
        qr.Verify(q => q.UpdateStatusAsync(SessionToken, QrSessionStatus.Consumed, It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    [TestMethod]
    [DataRow("/", true)]
    [DataRow("https://rp.example/callback", false)]
    public async Task Status_OnlyPlatformLoginRequiresCompletionPost(string returnUrl, bool completionRequired)
    {
        var (handler, _, _) = CreateHandler(NewSession(QrSessionStatus.Authenticated, returnUrl));
        var http = WithServices(Request(InitiatorSecret));
        http.Response.Body = new MemoryStream();

        var result = await handler.GetStatusAsync(http, SessionToken);
        await result.ExecuteAsync(http);

        using var body = System.Text.Json.JsonDocument.Parse(ReadBody(http));
        Assert.AreEqual(completionRequired, body.RootElement.GetProperty("completionRequired").GetBoolean());
        var url = body.RootElement.GetProperty("redirectUrl").GetString()!;
        StringAssert.StartsWith(url, completionRequired ? "/auth/qr-complete?session=" : "https://rp.example/callback?code=");
    }

    [TestMethod]
    public async Task Confirm_WithWrongNumber_FailsAndCancelsTheSession()
    {
        var (handler, qr, codes) = CreateHandler(NewSession(QrSessionStatus.Scanned));

        var result = await handler.ConfirmAsync(ConfirmRequest("17"));

        Assert.AreEqual(400, (result as IStatusCodeHttpResult)?.StatusCode);
        qr.Verify(q => q.UpdateStatusAsync(SessionToken, QrSessionStatus.Cancelled, It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
        qr.Verify(q => q.UpdateStatusAsync(SessionToken, QrSessionStatus.Authenticated, It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
        codes.Verify(c => c.IssueAsync(It.IsAny<AuthorizeValidationResult>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>()), Times.Never);
    }

    [TestMethod]
    public async Task Confirm_WithoutNumber_Fails()
    {
        var (handler, qr, codes) = CreateHandler(NewSession(QrSessionStatus.Scanned));

        var result = await handler.ConfirmAsync(ConfirmRequest(null));

        Assert.AreEqual(400, (result as IStatusCodeHttpResult)?.StatusCode);
        qr.Verify(q => q.UpdateStatusAsync(SessionToken, QrSessionStatus.Authenticated, It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
        codes.Verify(c => c.IssueAsync(It.IsAny<AuthorizeValidationResult>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>()), Times.Never);
    }

    [TestMethod]
    public async Task Confirm_ForASessionWithoutStoredNumber_Fails()
    {
        var legacy = NewSession(QrSessionStatus.Scanned);
        legacy.MatchCode = null;
        var (handler, _, codes) = CreateHandler(legacy);

        var result = await handler.ConfirmAsync(ConfirmRequest("42"));

        Assert.AreEqual(400, (result as IStatusCodeHttpResult)?.StatusCode);
        codes.Verify(c => c.IssueAsync(It.IsAny<AuthorizeValidationResult>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>()), Times.Never);
    }

    [TestMethod]
    public async Task Confirm_WithTheRightNumber_IssuesTheCode()
    {
        // The confirming user must exist and be active (deactivated users cannot confirm QR logins).
        var db = TestDataSeeder.CreateInMemoryDb();
        var user = new User { TenantId = Guid.NewGuid(), Username = "phone-user", Email = "phone@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var (handler, qr, codes) = CreateHandler(NewSession(QrSessionStatus.Scanned), db);

        var result = await handler.ConfirmAsync(ConfirmRequest(" 42 ", user.Id));

        Assert.IsFalse(result is IStatusCodeHttpResult { StatusCode: >= 400 }, "confirm with the right number should succeed");
        codes.Verify(c => c.IssueAsync(It.IsAny<AuthorizeValidationResult>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>()), Times.Once);
        qr.Verify(q => q.UpdateStatusAsync(SessionToken, QrSessionStatus.Authenticated, It.IsAny<Guid?>(), "issued-code"), Times.Once);
    }

    // ---- /auth/qr page --------------------------------------------------------------------------------------

    private static (QrModel Model, DefaultHttpContext Http) CreateQrPage(QrLoginSession session, string? initiatorSecret, string query)
    {
        var qr = new Mock<IQrLoginService>();
        qr.Setup(q => q.GetSessionAsync(SessionToken)).ReturnsAsync(session);
        qr.Setup(q => q.BuildMobileUrl(SessionToken)).Returns(MobileUrl);
        var generator = new Mock<IQrCodeGenerator>();
        generator.Setup(g => g.GenerateQrCodeDataUri(MobileUrl)).Returns("data:image/png;base64,REAL");

        var http = Request(initiatorSecret);
        http.Request.QueryString = new QueryString(query);
        var model = new QrModel(qr.Object, generator.Object, TestDataSeeder.CreateInMemoryDb(),
            Options.Create(new QrLoginOptions { Enabled = true }), NullLogger<QrModel>.Instance)
        {
            PageContext = new PageContext(new ActionContext(http, new RouteData(), new ActionDescriptor()))
        };
        return (model, http);
    }

    [TestMethod]
    public async Task QrPage_IgnoresQrImageFromQueryString_AndRegeneratesItFromTheSession()
    {
        var evil = Uri.EscapeDataString("data:image/png;base64,ATTACKER");
        var (model, _) = CreateQrPage(NewSession(QrSessionStatus.Pending), InitiatorSecret, $"?token={SessionToken}&qr={evil}");

        await model.OnGetAsync();

        Assert.IsNull(model.ErrorMessage);
        Assert.AreEqual("data:image/png;base64,REAL", model.QrCodeDataUri);
        Assert.AreEqual(MatchCode, model.MatchCode);
    }

    [TestMethod]
    public async Task QrPage_InAnotherBrowser_ShowsNeitherQrCodeNorNumber()
    {
        var evil = Uri.EscapeDataString("data:image/png;base64,ATTACKER");
        var (model, _) = CreateQrPage(NewSession(QrSessionStatus.Pending), initiatorSecret: null, $"?token={SessionToken}&qr={evil}");

        await model.OnGetAsync();

        Assert.IsNotNull(model.ErrorMessage);
        Assert.IsNull(model.QrCodeDataUri);
        Assert.IsNull(model.MatchCode);
    }

    // ---- session creation -----------------------------------------------------------------------------------

    [TestMethod]
    public async Task CreateSession_StoresOnlyTheHashOfTheInitiatorSecret_AndATwoDigitNumber()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenants = MockTenantAccessor.CreateWithDefaultTenant();
        var service = new QrLoginService(db, Options.Create(new QrLoginOptions { BaseUrl = "https://idp.example" }), tenants);

        var created = await service.CreateSessionAsync("app", "/", "challenge", "S256", "", null, "openid",
            new QrInitiatorInfo("203.0.113.7", "Mozilla/5.0 (Windows NT 10.0) Chrome/130.0"));

        var stored = db.QrLoginSessions.Single();
        Assert.AreEqual(CryptoHelper.ComputeSha256Hex(created.InitiatorSecret), stored.InitiatorSecretHash);
        Assert.AreNotEqual(created.InitiatorSecret, stored.InitiatorSecretHash);
        StringAssert.Matches(created.MatchCode, new System.Text.RegularExpressions.Regex("^[0-9]{2}$"));
        Assert.AreEqual(created.MatchCode, stored.MatchCode);
        Assert.AreEqual("203.0.113.7", stored.InitiatorIpAddress);
        Assert.IsFalse(created.MobileUrl.Contains(created.MatchCode + "&", StringComparison.Ordinal) || created.MobileUrl.Contains("match", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(created.MobileUrl.Contains(created.InitiatorSecret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void IssuedCookie_IsHostPrefixedSecureHttpOnly()
    {
        var http = new DefaultHttpContext();
        QrInitiatorBinding.Issue(http, SessionToken, InitiatorSecret, DateTimeOffset.UtcNow.AddMinutes(5));

        var setCookie = http.Response.Headers.SetCookie.ToString();
        StringAssert.StartsWith(setCookie, "__Host-");
        StringAssert.Contains(setCookie, "secure");
        StringAssert.Contains(setCookie, "httponly");
        StringAssert.Contains(setCookie, "path=/");
        Assert.IsFalse(setCookie.Contains("domain=", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36", "Chrome on Windows")]
    [DataRow("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15", "Safari on macOS")]
    [DataRow("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox on Linux")]
    [DataRow("", "Unknown browser")]
    public void SummarizeUserAgent_DescribesBrowserAndOs(string ua, string expected) =>
        Assert.AreEqual(expected, QrInitiatorBinding.SummarizeUserAgent(ua));

    private static DefaultHttpContext WithServices(DefaultHttpContext http, IAuthenticationService? auth = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAntiforgery();
        services.AddSingleton(auth ?? Mock.Of<IAuthenticationService>());
        services.AddSingleton(Mock.Of<IUserAccountService>());
        http.RequestServices = services.BuildServiceProvider();
        return http;
    }

    private static string ReadBody(DefaultHttpContext http)
    {
        http.Response.Body.Position = 0;
        return new StreamReader(http.Response.Body).ReadToEnd();
    }
}
