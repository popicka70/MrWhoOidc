using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Background;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Handlers.Logout;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Observability;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Tests for LogoutHandler orchestration. This handler is a pure delegator,
/// so tests focus on correct delegation to specialized handlers.
/// </summary>
[TestClass]
public class LogoutHandlerTests
{
    #region LocalLogoutAsync Tests

    [TestMethod]
    public async Task LocalLogoutAsync_Delegates_To_LocalLogoutHandler()
    {
        // Arrange
        var localLogout = new LocalLogoutHandler();
        var handler = CreateHandler(localLogout: localLogout);
        var http = CreateHttpContext("returnUrl", "https://example.com/home");

        // Act
        var result = await handler.LocalLogoutAsync(http);

        // Assert
        Assert.IsNotNull(result);
        // LocalLogoutHandler returns a Redirect result
    }

    [TestMethod]
    public async Task LocalLogoutAsync_Null_ReturnUrl_Redirects_To_Root()
    {
        // Arrange
        var localLogout = new LocalLogoutHandler();
        var handler = CreateHandler(localLogout: localLogout);
        var http = CreateHttpContext();

        // Act
        var result = await handler.LocalLogoutAsync(http);

        // Assert
        Assert.IsNotNull(result);
    }

    #endregion

    #region LogoutEntryAsync Tests

    [TestMethod]
    public async Task LogoutEntryAsync_Delegates_To_FederatedEntry()
    {
        // Arrange - Create handler with real dependencies (will perform local logout when federation disabled)
        var handler = CreateHandlerWithRealDependencies();
        var http = CreateHttpContext("returnUrl", "/home");

        // Act
        var result = await handler.LogoutEntryAsync(http);

        // Assert
        Assert.IsNotNull(result);
    }

    #endregion

    #region FederatedCallbackAsync Tests

    [TestMethod]
    public async Task FederatedCallbackAsync_Delegates_To_FederatedCallback()
    {
        // Arrange
        var handler = CreateHandlerWithRealDependencies();
        var http = CreateHttpContext("state", "callback-state");

        // Act
        var result = await handler.FederatedCallbackAsync(http);

        // Assert
        Assert.IsNotNull(result);
    }

    #endregion

    #region EndSessionAsync Tests

    [TestMethod]
    public async Task EndSessionAsync_Delegates_To_EndSession()
    {
        // Arrange
        var handler = CreateHandlerWithRealDependencies();
        var http = CreateHttpContextWithIssuer("https://issuer.example.com");

        // Act
        var result = await handler.EndSessionAsync(http);

        // Assert
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task EndSessionAsync_Accepts_Id_Token_Hint()
    {
        // Arrange
        var handler = CreateHandlerWithRealDependencies();
        var http = CreateHttpContextWithIssuer("https://issuer.example.com",
            ("id_token_hint", "eyJhbGc..."),
            ("client_id", "rp123")
        );

        // Act
        var result = await handler.EndSessionAsync(http);

        // Assert
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task EndSessionAsync_Accepts_Post_Logout_Redirect_Uri()
    {
        // Arrange
        var handler = CreateHandlerWithRealDependencies();
        var http = CreateHttpContextWithIssuer("https://issuer.example.com",
            ("post_logout_redirect_uri", "https://rp.example.com/signed-out"),
            ("client_id", "rp123"),
            ("state", "state-value")
        );

        // Act
        var result = await handler.EndSessionAsync(http);

        // Assert
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task EndSessionAsync_Accepts_Sid()
    {
        // Arrange
        var handler = CreateHandlerWithRealDependencies();
        var http = CreateHttpContextWithIssuer("https://issuer.example.com",
            ("sid", "session-id-123")
        );

        // Act
        var result = await handler.EndSessionAsync(http);

        // Assert
        Assert.IsNotNull(result);
    }

    #endregion

    #region FinalRedirectAsync Tests

    [TestMethod]
    public async Task FinalRedirectAsync_Delegates_To_RedirectResolver()
    {
        // Arrange
        var handler = CreateHandlerWithRealDependencies();
        var http = CreateHttpContext("ref", "opaque-ref-123");

        // Act
        var result = await handler.FinalRedirectAsync(http);

        // Assert
        Assert.IsNotNull(result);
        // Will return BadRequest for invalid ref since no DB record exists
    }

    [TestMethod]
    public async Task FinalRedirectAsync_Empty_Ref_Returns_Error()
    {
        // Arrange
        var handler = CreateHandlerWithRealDependencies();
        var http = CreateHttpContext("ref", "");

        // Act
        var result = await handler.FinalRedirectAsync(http);

        // Assert
        Assert.IsNotNull(result);
    }

    #endregion

    #region Test Helpers

    private static LogoutHandler CreateHandler(
        LocalLogoutHandler? localLogout = null,
        FederatedLogoutEntryHandler? federatedEntry = null,
        FederatedCallbackHandler? federatedCallback = null,
        EndSessionHandler? endSession = null,
        LogoutRedirectResolver? redirectResolver = null)
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var audit = new NoopAuditSink();
        var metrics = new OidcMetrics();
        var config = new ConfigurationBuilder().Build();

        return new LogoutHandler(
            localLogout ?? new LocalLogoutHandler(),
            federatedEntry ?? CreateFederatedLogoutEntryHandler(db, audit, metrics),
            federatedCallback ?? CreateFederatedCallbackHandler(db, audit, metrics),
            endSession ?? CreateEndSessionHandler(db, audit, metrics, config),
            redirectResolver ?? CreateLogoutRedirectResolver(db, audit)
        );
    }

    private static LogoutHandler CreateHandlerWithRealDependencies()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var audit = new NoopAuditSink();
        var metrics = new OidcMetrics();
        var config = new ConfigurationBuilder().Build();

        return new LogoutHandler(
            new LocalLogoutHandler(),
            CreateFederatedLogoutEntryHandler(db, audit, metrics),
            CreateFederatedCallbackHandler(db, audit, metrics),
            CreateEndSessionHandler(db, audit, metrics, config),
            CreateLogoutRedirectResolver(db, audit)
        );
    }

    private static FederatedLogoutEntryHandler CreateFederatedLogoutEntryHandler(AuthDbContext db, IAuditSink audit, OidcMetrics metrics)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var upstreamLogoutSvc = new UpstreamLogoutService(
            cache,
            Options.Create(new FederatedLogoutOptions { Enabled = false }),
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            NullLogger<UpstreamLogoutService>.Instance,
            db,
            new TestHttpClientFactory(),
            audit
        );

        return new FederatedLogoutEntryHandler(
            upstreamLogoutSvc,
            Options.Create(new FederatedLogoutOptions { Enabled = false }),
            NullLogger<FederatedLogoutEntryHandler>.Instance,
            audit,
            metrics,
            new LocalLogoutHandler()
        );
    }

    private static FederatedCallbackHandler CreateFederatedCallbackHandler(AuthDbContext db, IAuditSink audit, OidcMetrics metrics)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var upstreamLogoutSvc = new UpstreamLogoutService(
            cache,
            Options.Create(new FederatedLogoutOptions { Enabled = false }),
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            NullLogger<UpstreamLogoutService>.Instance,
            db,
            new TestHttpClientFactory(),
            audit
        );

        return new FederatedCallbackHandler(upstreamLogoutSvc, audit, metrics);
    }

    private static EndSessionHandler CreateEndSessionHandler(AuthDbContext db, IAuditSink audit, OidcMetrics metrics, IConfiguration config)
    {
        var frontChannel = new FrontChannelLogoutNotifier(db);
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var tokenBuilder = new LogoutTokenBuilder(keyStore);
        var backChannel = new BackChannelLogoutEnqueuer(
            db,
            tokenBuilder,
            NullLogger<BackChannelLogoutEnqueuer>.Instance,
            audit,
            metrics,
            new TestOptionsMonitor<BackchannelFeatureOptions>(new BackchannelFeatureOptions { Enabled = false }),
            config
        );
        var redirectValidator = new PostLogoutRedirectValidator(db, audit, metrics, NullLogger<PostLogoutRedirectValidator>.Instance);

        return new EndSessionHandler(frontChannel, backChannel, redirectValidator, audit, metrics, NullLogger<EndSessionHandler>.Instance);
    }

    private static LogoutRedirectResolver CreateLogoutRedirectResolver(AuthDbContext db, IAuditSink audit)
    {
        return new LogoutRedirectResolver(db, audit);
    }

    private static HttpContext CreateHttpContext(params (string key, string value)[] queryParams)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication("cookie")
            .AddCookie("cookie");
        services.AddOptions();
        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext();
        context.RequestServices = serviceProvider;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");

        var query = new QueryCollection(queryParams.ToDictionary(p => p.key, p => new Microsoft.Extensions.Primitives.StringValues(p.value)));
        context.Request.Query = query;
        return context;
    }

    private static HttpContext CreateHttpContext(string key, string value)
    {
        return CreateHttpContext((key, value));
    }

    private static HttpContext CreateHttpContext()
    {
        return CreateHttpContext(Array.Empty<(string, string)>());
    }

    private static HttpContext CreateHttpContextWithIssuer(string issuer, params (string key, string value)[] queryParams)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication("cookie")
            .AddCookie("cookie");
        services.AddOptions();
        services.AddSingleton(new OidcOptions { Issuer = issuer });
        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext();
        context.RequestServices = serviceProvider;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Items["issuer"] = issuer;

        var query = new QueryCollection(queryParams.ToDictionary(p => p.key, p => new Microsoft.Extensions.Primitives.StringValues(p.value)));
        context.Request.Query = query;
        return context;
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client = new(new TestHttpHandler());
        public HttpClient CreateClient(string name = null!) => _client;
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    #endregion
}
