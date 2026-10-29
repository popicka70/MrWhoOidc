using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// F1: the sign-in paths create the server-side OP session and write its id into the cookie as the <c>sid</c>.
/// Every path goes through <see cref="OpSessionSignIn"/>, so the paths cannot drift apart.
/// </summary>
[TestClass]
public sealed class OpSessionSignInTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid AccountId = Guid.NewGuid();

    [TestMethod]
    public async Task TheSignIn_CreatesTheSession_AndAddsTheSidToTheCookie()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, UserId.ToString()),
            new(OidcConstants.Claims.Amr, "pwd"),
            new(OidcConstants.Claims.Acr, OidcConstants.AcrValues.Password),
            new(OidcConstants.Claims.Idp, "local")
        };
        var authTime = DateTimeOffset.UtcNow.AddMinutes(-3);

        var sessionId = await OpSessionSignIn.StartAsync(
            CreateHttpContext(service), claims, UserId, AccountId, authTime);

        Assert.IsNotNull(sessionId, "the sign-in must start a session");
        var sid = claims.Single(c => c.Type == OidcConstants.Claims.Sid).Value;
        Assert.AreEqual(sessionId.Value.ToString("N"), sid, "the sid is the unhyphenated id of the session");

        var session = await db.OpSessions.SingleAsync();
        Assert.AreEqual(TenantId, session.TenantId);
        Assert.AreEqual(UserId, session.UserId);
        Assert.AreEqual(AccountId, session.UserAccountId);
        Assert.AreEqual(OidcConstants.AcrValues.Password, session.Acr);
        Assert.AreEqual("pwd", session.AmrJson);
        Assert.AreEqual("local", session.UpstreamIdp);
        Assert.AreEqual(authTime.ToUnixTimeSeconds(), session.AuthTime.ToUnixTimeSeconds());
    }

    [TestMethod]
    public async Task ASignIn_CreatesExactlyOneSession_EachTime()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var first = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var second = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId.ToString()) };

        var firstId = await OpSessionSignIn.StartAsync(CreateHttpContext(service), first, UserId, null, DateTimeOffset.UtcNow);
        var secondId = await OpSessionSignIn.StartAsync(CreateHttpContext(service), second, UserId, null, DateTimeOffset.UtcNow);

        Assert.IsNotNull(firstId);
        Assert.IsNotNull(secondId);
        Assert.AreNotEqual(firstId.Value, secondId.Value, "a second sign-in is a second session");
        Assert.AreEqual(2, await db.OpSessions.CountAsync());
    }

    [TestMethod]
    public async Task TheStoreIsOff_TheSignInKeepsItsCurrentBehaviour()
    {
        var (service, db) = CreateService(o => o.Enabled = false);
        await using var _db = db;

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId.ToString()) };

        var sessionId = await OpSessionSignIn.StartAsync(
            CreateHttpContext(BuildServices(service, new OpSessionOptions { Enabled = false })),
            claims,
            UserId,
            null,
            DateTimeOffset.UtcNow);

        Assert.IsNull(sessionId);
        Assert.IsFalse(claims.Any(c => c.Type == OidcConstants.Claims.Sid), "no session, no sid");
        Assert.AreEqual(0, await db.OpSessions.CountAsync());
    }

    [TestMethod]
    public async Task ASessionThatCannotBeCreated_DoesNotFailTheSignIn()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        await using var _db = db;

        var failing = new Mock<IOpSessionService>();
        failing.Setup(s => s.CreateAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is gone"));

        var services = new ServiceCollection();
        services.AddSingleton(failing.Object);
        services.AddSingleton(Options.Create(new OpSessionOptions { Enabled = true }));
        services.AddSingleton<ITenantAccessor>(MockTenantAccessor.CreateWithTenant(TenantId, "acme"));
        var provider = services.BuildServiceProvider();

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId.ToString()) };

        var sessionId = await OpSessionSignIn.StartAsync(CreateHttpContext(provider), claims, UserId, null, DateTimeOffset.UtcNow);

        Assert.IsNull(sessionId);
        Assert.IsFalse(claims.Any(c => c.Type == OidcConstants.Claims.Sid));
    }

    [TestMethod]
    public async Task TheTenantSwitch_CreatesANewSessionInTheNewTenant_AndLeavesTheOldOneAlone()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        // The session of the old tenant.
        var oldSession = await service.CreateAsync(TenantId, UserId);

        // The switch: the session belongs to the new tenant, and the cookie carries its id.
        var newTenantId = Guid.NewGuid();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var sessionId = await OpSessionSignIn.StartAsync(
            CreateHttpContext(service), claims, UserId, null, DateTimeOffset.UtcNow, tenantId: newTenantId);

        Assert.IsNotNull(sessionId);
        Assert.AreNotEqual(oldSession.Id, sessionId.Value, "the switch is a new session");

        var sessions = await db.OpSessions.AsNoTracking().ToListAsync();
        Assert.AreEqual(2, sessions.Count);
        Assert.IsNull(sessions.Single(s => s.Id == oldSession.Id).RevokedAt, "the old session stays revocable in its own tenant");
        Assert.AreEqual(newTenantId, sessions.Single(s => s.Id == sessionId.Value).TenantId);
    }

    [TestMethod]
    public void TheCurrentSessionId_IsReadFromTheSidClaim()
    {
        var id = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(OidcConstants.Claims.Sid, id.ToString("N")) }, "Cookies"));

        Assert.AreEqual(id, OpSessionSignIn.CurrentSessionId(principal));
        Assert.IsNull(OpSessionSignIn.CurrentSessionId(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.IsNull(OpSessionSignIn.CurrentSessionId(null));
    }

    private static (IOpSessionService service, AuthDbContext db) CreateService(Action<OpSessionOptions>? configure = null)
    {
        var db = TestDataSeeder.CreateInMemoryDb();

        var settings = new Mock<ITenantSettingsService>();
        settings.Setup(s => s.GetCurrentTenantSettingsAsync()).ReturnsAsync(new TenantSettings());

        var options = new OpSessionOptions();
        configure?.Invoke(options);

        var service = new OpSessionService(
            db,
            MockTenantAccessor.CreateWithTenant(TenantId, "acme"),
            settings.Object,
            Options.Create(options),
            NullLogger<OpSessionService>.Instance);

        return (service, db);
    }

    private static DefaultHttpContext CreateHttpContext(IOpSessionService service)
        => CreateHttpContext(BuildServices(service, new OpSessionOptions { Enabled = true }));

    private static DefaultHttpContext CreateHttpContext(IServiceProvider services)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("idp.example");
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        http.Request.Headers.UserAgent = "Mozilla/5.0 (X11; Linux x86_64) TestBrowser/1.0";
        return http;
    }

    private static ServiceProvider BuildServices(IOpSessionService service, OpSessionOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(service);
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<ITenantAccessor>(MockTenantAccessor.CreateWithTenant(TenantId, "acme"));
        services.AddSingleton<IAuditSink>(new LoggerAuditSink(
            NullLogger<LoggerAuditSink>.Instance,
            Options.Create(new AuditOptions())));
        return services.BuildServiceProvider();
    }
}
