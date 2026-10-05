using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Background;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Handlers.Logout;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// 2026-10-04 assessment: /connect/endsession (GET only) and /logout ended the session for any request, so a link,
/// image or cross-site form on any page could log the user out. Without a verified id_token_hint for the
/// signed-in user, the user must now confirm through an antiforgery-protected POST.
/// </summary>
[TestClass]
public sealed class LogoutCsrfTests
{
    private const string Issuer = "https://issuer.example.com";

    private sealed class Fixture
    {
        public required AuthDbContext Db { get; init; }
        public required EndSessionHandler EndSession { get; init; }
        public required KeyStore KeyStore { get; init; }
        public required IServiceProvider Services { get; init; }
        public required Mock<IAuthenticationService> Auth { get; init; }
    }

    private static Fixture CreateFixture()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var audit = new NoopAuditSink();
        var metrics = new OidcEndpointMetrics();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Options.Create(new KeyRotationOptions()));
        var backChannel = new BackChannelLogoutEnqueuer(
            db,
            Mock.Of<MrWhoOidc.Auth.Services.Token.ILogoutTokenService>(),
            NullLogger<BackChannelLogoutEnqueuer>.Instance,
            audit,
            metrics,
            Mock.Of<IOptionsMonitor<BackchannelFeatureOptions>>(m => m.CurrentValue == new BackchannelFeatureOptions { Enabled = false }),
            new ConfigurationBuilder().Build());
        var endSession = new EndSessionHandler(
            new FrontChannelLogoutNotifier(),
            backChannel,
            TestLogoutTargetResolverFactory.Create(db, keyStore),
            new PostLogoutRedirectValidator(db, audit, metrics, NullLogger<PostLogoutRedirectValidator>.Instance),
            TestTokenValidatorFactory.Create(keyStore),
            audit,
            metrics,
            NullLogger<EndSessionHandler>.Instance);

        var auth = new Mock<IAuthenticationService>();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(auth.Object);
        services.AddAntiforgery();
        return new Fixture { Db = db, EndSession = endSession, KeyStore = keyStore, Services = services.BuildServiceProvider(), Auth = auth };
    }

    private static ClaimsPrincipal Session(Guid userId)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Cookies"));

    private static DefaultHttpContext Request(Fixture f, string method, ClaimsPrincipal? user, IDictionary<string, string>? values = null, string path = "/connect/endsession")
    {
        var http = new DefaultHttpContext { RequestServices = f.Services };
        http.Request.Method = method;
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("issuer.example.com");
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();
        if (user is not null) http.User = user;
        var dict = (values ?? new Dictionary<string, string>()).ToDictionary(kv => kv.Key, kv => new StringValues(kv.Value));
        if (method == "POST")
        {
            http.Request.ContentType = "application/x-www-form-urlencoded";
            http.Request.Form = new FormCollection(dict);
        }
        else
        {
            http.Request.Query = new QueryCollection(dict);
        }
        return http;
    }

    private static Task<string> HintAsync(Fixture f, Guid sub)
        => TestJwtServiceFactory.Create(f.KeyStore).CreateJwtAsync(Issuer, "spa", [new Claim("sub", sub.ToString())], DateTimeOffset.UtcNow.AddMinutes(5), tokenType: "JWT");

    private static async Task<IResult> EndSessionAsync(Fixture f, HttpContext http)
        => await f.EndSession.HandleAsync(http, await LogoutRequest.FromRequestAsync(http.Request), Issuer);

    private void AssertConfirmationShown(IResult result, Fixture f)
    {
        Assert.IsInstanceOfType<ContentHttpResult>(result);
        var content = ((ContentHttpResult)result).ResponseContent!;
        StringAssert.Contains(content, "Sign out?");
        StringAssert.Contains(content, LogoutConfirmationPage.ConfirmField);
        StringAssert.Contains(content, "__RequestVerificationToken");
        f.Auth.Verify(a => a.SignOutAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<AuthenticationProperties?>()), Times.Never, "no sign-out before confirmation");
    }

    private static void AssertSignedOut(Fixture f)
        => f.Auth.Verify(a => a.SignOutAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<AuthenticationProperties?>()), Times.AtLeastOnce);

    [TestMethod]
    [DataRow("GET")]
    [DataRow("POST")]
    public async Task EndSession_WithoutHint_AsksForConfirmation(string method)
    {
        var f = CreateFixture();
        var http = Request(f, method, Session(Guid.NewGuid()));

        AssertConfirmationShown(await EndSessionAsync(f, http), f);
    }

    [TestMethod]
    public async Task EndSession_WithHintForTheSessionUser_LogsOutWithoutConfirmation()
    {
        var f = CreateFixture();
        var userId = Guid.NewGuid();
        var http = Request(f, "GET", Session(userId), new Dictionary<string, string> { ["id_token_hint"] = await HintAsync(f, userId) });

        await EndSessionAsync(f, http);

        AssertSignedOut(f);
    }

    [TestMethod]
    public async Task EndSession_WithHintForAnotherUser_AsksForConfirmation()
    {
        var f = CreateFixture();
        var http = Request(f, "GET", Session(Guid.NewGuid()), new Dictionary<string, string> { ["id_token_hint"] = await HintAsync(f, Guid.NewGuid()) });

        AssertConfirmationShown(await EndSessionAsync(f, http), f);
    }

    [TestMethod]
    public async Task EndSession_CrossSitePostWithoutVisibleSession_AsksForConfirmation()
    {
        // A cross-site POST does not carry the SameSite=Lax session cookie, yet the sign-out would still clear it.
        var f = CreateFixture();
        var http = Request(f, "POST", null, new Dictionary<string, string> { ["id_token_hint"] = await HintAsync(f, Guid.NewGuid()) });

        AssertConfirmationShown(await EndSessionAsync(f, http), f);
    }

    [TestMethod]
    public async Task EndSession_PostWithHintForTheSessionUser_IsSupported()
    {
        var f = CreateFixture();
        var userId = Guid.NewGuid();
        var http = Request(f, "POST", Session(userId), new Dictionary<string, string> { ["id_token_hint"] = await HintAsync(f, userId) });

        await EndSessionAsync(f, http);

        AssertSignedOut(f);
    }

    [TestMethod]
    public async Task EndSession_ConfirmationPost_WithAntiforgeryToken_LogsOut()
    {
        var f = CreateFixture();
        var user = Session(Guid.NewGuid());
        var http = await ConfirmedPostAsync(f, user, withToken: true);

        await EndSessionAsync(f, http);

        AssertSignedOut(f);
    }

    [TestMethod]
    public async Task EndSession_ConfirmationPost_WithoutAntiforgeryToken_IsNotEnough()
    {
        var f = CreateFixture();
        var http = await ConfirmedPostAsync(f, Session(Guid.NewGuid()), withToken: false);

        AssertConfirmationShown(await EndSessionAsync(f, http), f);
    }

    private static Task<DefaultHttpContext> ConfirmedPostAsync(Fixture f, ClaimsPrincipal user, bool withToken, string path = "/connect/endsession")
    {
        var antiforgery = f.Services.GetRequiredService<IAntiforgery>();
        var options = f.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        var tokenContext = new DefaultHttpContext { RequestServices = f.Services, User = user };
        var tokens = antiforgery.GetTokens(tokenContext);

        var form = new Dictionary<string, string> { [LogoutConfirmationPage.ConfirmField] = "1", ["returnUrl"] = "/" };
        if (withToken) form[options.FormFieldName] = tokens.RequestToken!;
        var http = Request(f, "POST", user, form, path);
        http.Request.Headers.Cookie = $"{options.Cookie.Name}={tokens.CookieToken}";
        return Task.FromResult(http);
    }

    private static LogoutHandler CreateLogoutHandler(Fixture f)
    {
        var fedOptions = Options.Create(new FederatedLogoutOptions { Enabled = false });
        var upstream = new UpstreamLogoutService(new MemoryCache(new MemoryCacheOptions()), fedOptions,
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), NullLogger<UpstreamLogoutService>.Instance,
            f.Db, Mock.Of<IHttpClientFactory>(), new NoopAuditSink());
        var local = new LocalLogoutHandler();
        return new LogoutHandler(
            local,
            new FederatedLogoutEntryHandler(upstream, fedOptions, NullLogger<FederatedLogoutEntryHandler>.Instance, new NoopAuditSink(), new OidcEndpointMetrics(), local),
            new FederatedCallbackHandler(upstream, new NoopAuditSink(), new OidcEndpointMetrics()),
            f.EndSession,
            new LogoutRedirectResolver(f.Db, new NoopAuditSink()));
    }

    [TestMethod]
    public async Task LocalLogout_Get_AsksForConfirmation()
    {
        var f = CreateFixture();
        var http = Request(f, "GET", Session(Guid.NewGuid()), new Dictionary<string, string> { ["returnUrl"] = "/" }, "/logout");

        AssertConfirmationShown(await CreateLogoutHandler(f).LogoutEntryAsync(http), f);
    }

    [TestMethod]
    public async Task LocalLogout_ConfirmedPost_LogsOut()
    {
        var f = CreateFixture();
        var http = await ConfirmedPostAsync(f, Session(Guid.NewGuid()), withToken: true, path: "/logout");

        var result = await CreateLogoutHandler(f).LogoutEntryAsync(http);

        Assert.IsInstanceOfType<RedirectHttpResult>(result);
        AssertSignedOut(f);
    }
}
