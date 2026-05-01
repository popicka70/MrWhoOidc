using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Tests for CIBA (Client Initiated Backchannel Authentication) - OpenID Connect CIBA Core 1.0.
/// Uses direct handler instantiation pattern (like DeviceAuthorizationTests) for reliability.
/// </summary>
[TestClass]
public sealed class CibaTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static CibaAuthenticationHandler CreateHandler(
        AuthDbContext? db = null,
        IClientStore? clients = null,
        IClientAssertionValidator? assertions = null,
        ITokenValidator? tokenValidator = null,
        IOptions<AuthOptions>? authOptions = null,
        ICibaNotificationService? notificationService = null)
    {
        var logger = NullLogger<CibaAuthenticationHandler>.Instance;

        db ??= CreateDb();
        clients ??= new StubClientStore();
        assertions ??= new StubClientAssertionValidator();
        tokenValidator ??= new StubTokenValidator();
        authOptions ??= Options.Create(new AuthOptions
        {
            EnableCiba = true,
            CibaAuthRequestLifetimeSeconds = 120,
            CibaPollingIntervalSeconds = 5,
            CibaTokenDeliveryModesSupported = ["poll", "ping"],
            CibaUserCodeParameterSupported = false
        });
        notificationService ??= new StubCibaNotificationService();

        var oidcOptions = new OidcOptions { Issuer = "https://test.example.com" };
        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = Guid.NewGuid(),
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        return new CibaAuthenticationHandler(oidcOptions, authOptions, db, clients, assertions, tokenValidator, tenantAccessor, notificationService, logger);
    }

    private static DefaultHttpContext CreateHttpContext(
        Dictionary<string, string>? formData = null,
        string? authorizationHeader = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddScoped<MrWhoOidc.Auth.MultiTenancy.ITenantAccessor, MrWhoOidc.Auth.MultiTenancy.TenantAccessor>();
        services.AddSingleton<MrWhoOidc.Auth.MultiTenancy.IMultiTenancyOptions>(new MrWhoOidc.Auth.MultiTenancy.MultiTenancyOptions());
        services.AddScoped<MrWhoOidc.Auth.MultiTenancy.IIssuerBuilder, MrWhoOidc.Auth.MultiTenancy.IssuerBuilder>();
        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext();
        context.RequestServices = serviceProvider;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Request.Path = "/bc-authorize";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Response.Body = new MemoryStream();

        if (authorizationHeader != null)
        {
            context.Request.Headers.Authorization = authorizationHeader;
        }

        if (formData != null)
        {
            var formCollection = new FormCollection(formData.ToDictionary(
                kvp => kvp.Key,
                kvp => new Microsoft.Extensions.Primitives.StringValues(kvp.Value)));
            context.Request.Form = formCollection;
        }

        return context;
    }

    #region Feature Flag Tests

    [TestMethod]
    public async Task HandleAsync_CibaDisabled_ReturnsNotFound()
    {
        var authOptions = Options.Create(new AuthOptions { EnableCiba = false });
        var handler = CreateHandler(authOptions: authOptions);
        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "test-client",
            ["login_hint"] = "user@example.com",
            ["scope"] = "openid"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(404, ctx.Response.StatusCode);
    }

    #endregion

    #region Client Authentication Tests

    [TestMethod]
    public async Task HandleAsync_MissingClientId_ReturnsInvalidRequest()
    {
        var handler = CreateHandler();
        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["login_hint"] = "user@example.com",
            ["scope"] = "openid"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_request"));
        Assert.IsTrue(body.Contains("client_id"));
    }

    [TestMethod]
    public async Task HandleAsync_UnknownClient_ReturnsInvalidClient()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "unknown-client",
            ["client_secret"] = "secret",
            ["login_hint"] = "user@example.com",
            ["scope"] = "openid"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_client"));
    }

    [TestMethod]
    public async Task HandleAsync_ClientAuthFailed_ReturnsInvalidClient()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        // ClientStore that fails authentication
        var failingClientStore = new StubClientStore(authenticates: false);

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            failingClientStore,
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "wrong-secret",
            ["login_hint"] = "user@example.com",
            ["scope"] = "openid"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_client"));
    }

    #endregion

    #region User Hint Tests

    [TestMethod]
    public async Task HandleAsync_NoUserHint_ReturnsInvalidRequest()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["scope"] = "openid"
            // No login_hint, login_hint_token, or id_token_hint
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_request"));
    }

    [TestMethod]
    public async Task HandleAsync_MultipleUserHints_ReturnsInvalidRequest()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["login_hint"] = "user@example.com",
            ["id_token_hint"] = "some.jwt.token",
            ["scope"] = "openid"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_request"));
    }

    [TestMethod]
    public async Task HandleAsync_LoginHintTokenWithoutClientJwks_ReturnsInvalidRequest()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId,
            PublicJwksJson = null
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var invalidJwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ1c2VyMSJ9.";
        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["login_hint_token"] = invalidJwt,
            ["scope"] = "openid"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_request"));
        Assert.IsTrue(body.Contains("login_hint_token"));
    }

    [TestMethod]
    public async Task HandleAsync_InvalidIdTokenHint_ReturnsInvalidRequest()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(ok: false),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["id_token_hint"] = "invalid.token.value",
            ["scope"] = "openid"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_request"));
        Assert.IsTrue(body.Contains("id_token_hint"));
    }

    [TestMethod]
    public async Task HandleAsync_LoginHintTokenWithWrongIssuer_ReturnsInvalidRequest()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var (loginHintToken, jwksSetJson) = CreateClientSignedJwt("other-client", "https://test.example.com", "user-1");

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId,
            PublicJwksUri = "https://client.example/jwks"
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance,
            new StubHttpClientFactory(jwksSetJson));

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["login_hint_token"] = loginHintToken,
            ["scope"] = "openid"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_request"));
        Assert.IsTrue(body.Contains("login_hint_token"));
    }

    [TestMethod]
    public async Task HandleAsync_LoginHintTokenWithWrongAudience_ReturnsInvalidRequest()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var (loginHintToken, jwksSetJson) = CreateClientSignedJwt("ciba-client", "https://other.example.com", "user-1");

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId,
            PublicJwksJson = jwksSetJson
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["login_hint_token"] = loginHintToken,
            ["scope"] = "openid"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_request"));
        Assert.IsTrue(body.Contains("login_hint_token"));
    }

    #endregion

    #region Scope Tests

    [TestMethod]
    public async Task HandleAsync_MissingOpenidScope_ReturnsInvalidScope()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["login_hint"] = "user@example.com",
            ["scope"] = "profile email" // Missing openid
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_scope"));
    }

    #endregion

    #region Successful Request Tests

    [TestMethod]
    public async Task HandleAsync_ValidRequest_ReturnsAuthReqIdAndExpiresIn()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var authOptions = Options.Create(new AuthOptions
        {
            EnableCiba = true,
            CibaAuthRequestLifetimeSeconds = 120,
            CibaPollingIntervalSeconds = 5
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            authOptions,
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["login_hint"] = "user@example.com",
            ["scope"] = "openid profile"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(200, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var json = JsonDocument.Parse(body);

        Assert.IsTrue(json.RootElement.TryGetProperty("auth_req_id", out var authReqId));
        Assert.IsFalse(string.IsNullOrEmpty(authReqId.GetString()));

        Assert.IsTrue(json.RootElement.TryGetProperty("expires_in", out var expiresIn));
        Assert.AreEqual(120, expiresIn.GetInt32());

        Assert.IsTrue(json.RootElement.TryGetProperty("interval", out var interval));
        Assert.AreEqual(5, interval.GetInt32());
    }

    [TestMethod]
    public async Task HandleAsync_ValidRequest_StoresRequestInDatabase()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["login_hint"] = "user@example.com",
            ["scope"] = "openid profile",
            ["binding_message"] = "Transaction 12345"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(200, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var json = JsonDocument.Parse(body);
        var authReqId = json.RootElement.GetProperty("auth_req_id").GetString();

        // Verify the request was persisted
        var storedEntry = await db.CibaAuthenticationRequests.FirstOrDefaultAsync(r => r.AuthReqId == authReqId);
        Assert.IsNotNull(storedEntry);
        Assert.AreEqual("ciba-client", storedEntry.ClientId);
        Assert.AreEqual("user@example.com", storedEntry.UserIdentifierHint);
        Assert.AreEqual("login_hint", storedEntry.HintType);
        Assert.AreEqual("Transaction 12345", storedEntry.BindingMessage);
        Assert.AreEqual(CibaRequestStatus.Pending, storedEntry.Status);
    }

    [TestMethod]
    public async Task HandleAsync_WithBasicAuth_Works()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("ciba-client:secret"));
        var ctx = CreateHttpContext(
            formData: new Dictionary<string, string>
            {
                ["login_hint"] = "user@example.com",
                ["scope"] = "openid"
            },
            authorizationHeader: $"Basic {basicAuth}");

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(200, ctx.Response.StatusCode);
    }

    #endregion

    #region Binding Message Tests

    [TestMethod]
    public async Task HandleAsync_BindingMessageTooLong_ReturnsError()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["login_hint"] = "user@example.com",
            ["scope"] = "openid",
            ["binding_message"] = new string('X', 201) // > 200 chars
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_binding_message"));
    }

    [TestMethod]
    public async Task HandleAsync_InvalidClientNotificationToken_ReturnsInvalidRequest()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions { EnableCiba = true }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["login_hint"] = "user@example.com",
            ["client_notification_token"] = "bad token with spaces",
            ["scope"] = "openid"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_request"));
        Assert.IsTrue(body.Contains("client_notification_token"));
    }

    [TestMethod]
    public async Task HandleAsync_PingOnlyMode_MissingClientNotificationToken_ReturnsInvalidRequest()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "ciba-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = tenantId,
            Slug = "test",
            IssuerUri = "https://test.example.com"
        });

        var handler = new CibaAuthenticationHandler(
            new OidcOptions { Issuer = "https://test.example.com" },
            Options.Create(new AuthOptions
            {
                EnableCiba = true,
                CibaTokenDeliveryModesSupported = ["ping"]
            }),
            db,
            new StubClientStore(),
            new StubClientAssertionValidator(),
            new StubTokenValidator(),
            tenantAccessor,
            new StubCibaNotificationService(),
            NullLogger<CibaAuthenticationHandler>.Instance);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "ciba-client",
            ["client_secret"] = "secret",
            ["login_hint"] = "user@example.com",
            ["scope"] = "openid"
        });

        var result = await handler.HandleAsync(ctx);

        await result.ExecuteAsync(ctx);
        Assert.AreEqual(400, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("invalid_request"));
        Assert.IsTrue(body.Contains("client_notification_token"));
    }

    #endregion

    #region Stub Implementations

    private sealed class StubClientStore : IClientStore
    {
        private readonly bool _authenticates;

        public StubClientStore(bool authenticates = true)
        {
            _authenticates = authenticates;
        }

        public Task<MrWhoOidc.Auth.Persistence.Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
            => Task.FromResult<MrWhoOidc.Auth.Persistence.Client?>(null);

        public Task<bool> ValidateClientSecretAsync(string clientId, string? secret, CancellationToken ct = default)
            => Task.FromResult(_authenticates);

        public IQueryable<MrWhoOidc.Auth.Persistence.Client> QueryClients(CancellationToken ct = default)
            => Enumerable.Empty<MrWhoOidc.Auth.Persistence.Client>().AsQueryable();

        public Task InvalidateClientCacheAsync(string clientId, Guid tenantId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ClientSecret?> GetPrimarySecretAsync(Guid clientRecordId, CancellationToken ct = default)
            => Task.FromResult<ClientSecret?>(null);

        public Task<List<ClientSecret>> GetActiveSecretsAsync(Guid clientRecordId, CancellationToken ct = default)
            => Task.FromResult(new List<ClientSecret>());

        public Task<ClientSecret> CreateSecretAsync(Guid clientRecordId, string secretValue, string? description, string? createdBy, DateTime? expiresAtUtc = null, CancellationToken ct = default)
            => Task.FromResult(new ClientSecret());

        public Task<bool> ActivateSecretAsync(Guid secretId, string activatedBy, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> SetPrimarySecretAsync(Guid secretId, string updatedBy, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> RevokeSecretAsync(Guid secretId, string revokedBy, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> RecordSecretUsageAsync(Guid secretId, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class StubClientAssertionValidator : IClientAssertionValidator
    {
        public Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class StubTokenValidator : ITokenValidator
    {
        private readonly bool _ok;
        private readonly ClaimsPrincipal? _principal;

        public StubTokenValidator(bool ok = true, string subject = "test-user")
        {
            _ok = ok;
            _principal = ok
                ? new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "test"))
                : null;
        }

        public Task<(bool ok, ClaimsPrincipal? principal, string? error)> ValidateAsync(string token, string issuer, CancellationToken ct = default, IEnumerable<string>? validAudiences = null)
            => Task.FromResult((_ok, _principal, _ok ? null : "invalid_token"));
    }

    private sealed class StubCibaNotificationService : ICibaNotificationService
    {
        public Task NotifyUserAsync(CibaAuthenticationRequest request, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendPingNotificationAsync(CibaAuthenticationRequest request, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static (string token, string jwksSetJson) CreateClientSignedJwt(string issuer, string audience, string subject)
    {
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(true);
        var kid = Guid.NewGuid().ToString("N");

        var publicJwkJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["alg"] = "RS256",
            ["kid"] = kid,
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent)
        });

        var privateJwkJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["alg"] = "RS256",
            ["kid"] = kid,
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
            ["d"] = Base64UrlEncoder.Encode(parameters.D),
            ["p"] = Base64UrlEncoder.Encode(parameters.P),
            ["q"] = Base64UrlEncoder.Encode(parameters.Q),
            ["dp"] = Base64UrlEncoder.Encode(parameters.DP),
            ["dq"] = Base64UrlEncoder.Encode(parameters.DQ),
            ["qi"] = Base64UrlEncoder.Encode(parameters.InverseQ)
        });

        var credentials = new SigningCredentials(new JsonWebKey(privateJwkJson), SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[] { new Claim("sub", subject) },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), $"{{\"keys\":[{publicJwkJson}]}}");
    }

    private sealed class StubHttpClientFactory(string responseBody) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler(responseBody));
    }

    private sealed class StubHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            });
    }

    #endregion
}
