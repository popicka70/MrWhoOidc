using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.TokenEndpoint.Grants;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Protocols;
using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class CibaIntegrationTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static async Task<Guid> CreateTestTenant(AuthDbContext db, string slug = "test")
    {
        var tenantId = Guid.NewGuid();
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Test Tenant",
            Slug = slug,
            Status = TenantStatus.Active
        };
        db.Tenants.Add(tenant);

        var realm = new Realm
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "default",
            DisplayName = "Default Realm",
            AllowUnconfirmedLogin = true
        };
        db.Realms.Add(realm);

        var signingKey = new SigningKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kid = $"test-key-{Guid.NewGuid():N}",
            Use = "sig",
            Alg = "RS256",
            JwkJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SigningKeys.Add(signingKey);

        await db.SaveChangesAsync();
        return tenantId;
    }

    private static DefaultHttpContext CreateHttpContextWithForm(string path, Dictionary<string, string> formValues)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("test.example.com");
        ctx.Request.Path = path;
        ctx.Request.Method = "POST";
        ctx.Request.Headers["Content-Type"] = "application/x-www-form-urlencoded";
        var body = new FormUrlEncodedContent(formValues).ReadAsStringAsync().GetAwaiter().GetResult();
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new System.IO.MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        ctx.Request.Body.Seek(0, System.IO.SeekOrigin.Begin);
        ctx.Response.Body = new System.IO.MemoryStream();
        ctx.RequestServices = services.BuildServiceProvider();
        return ctx;
    }

    private sealed class StubClientStore : IClientStore
    {
        private readonly MrWhoOidc.Auth.Persistence.Client _client;
        public StubClientStore(MrWhoOidc.Auth.Persistence.Client client)
        {
            _client = client;
        }
        public Task<MrWhoOidc.Auth.Persistence.Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default) => Task.FromResult(_client.ClientId == clientId ? _client : null);
        public Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ValidateClientCredentialsAsync(string clientId, string clientSecret, CancellationToken ct = default) => Task.FromResult(true);
        public IQueryable<MrWhoOidc.Auth.Persistence.Client> QueryClients(CancellationToken ct = default) => new[] { _client }.AsQueryable();
        public Task InvalidateClientCacheAsync(string clientId, Guid tenantId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<MrWhoOidc.Auth.Persistence.ClientSecret?> GetPrimarySecretAsync(Guid clientId, CancellationToken ct = default) => Task.FromResult<MrWhoOidc.Auth.Persistence.ClientSecret?>(null);
        public Task<List<MrWhoOidc.Auth.Persistence.ClientSecret>> GetActiveSecretsAsync(Guid clientId, CancellationToken ct = default) => Task.FromResult(new List<MrWhoOidc.Auth.Persistence.ClientSecret>());
        public Task<MrWhoOidc.Auth.Persistence.ClientSecret> CreateSecretAsync(Guid clientId, string secret, string? description = null, CancellationToken ct = default) => Task.FromResult(new MrWhoOidc.Auth.Persistence.ClientSecret());
        public Task<ClientSecret> CreateSecretAsync(Guid clientRecordId, string secretValue, string? description, string? createdBy, DateTime? expiresAtUtc = null, CancellationToken ct = default) => Task.FromResult(new ClientSecret());
        public Task<bool> ActivateSecretAsync(Guid secretId, string activatedBy, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetPrimarySecretAsync(Guid secretId, string updatedBy, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RevokeSecretAsync(Guid secretId, string revokedBy, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RecordSecretUsageAsync(Guid secretId, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class StubNotificationService : ICibaNotificationService
    {
        public bool WasCalled; 
        public string? LastAuthReqId;
        public Task NotifyUserAsync(CibaAuthenticationRequest request, CancellationToken ct = default)
        {
            WasCalled = true;
            LastAuthReqId = request.AuthReqId;
            return Task.CompletedTask;
        }

        public Task SendPingNotificationAsync(CibaAuthenticationRequest request, CancellationToken cancellationToken = default)
        {
            // For tests we just record the call; no external HTTP is performed.
            WasCalled = true;
            LastAuthReqId = request.AuthReqId;
            return Task.CompletedTask;
        }
    }

    private sealed class StubTokenService : ITokenService
    {
        public Task<(bool ok, object? payload, string? error, int status)> CreateDeviceCodeTokenAsync(string clientId, Guid userId, string[] scopes, string audience, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default)
        {
            var payload = new { access_token = "test_access_token", token_type = "Bearer", expires_in = 3600 };
            return Task.FromResult((true, (object?)payload, (string?)null, 200));
        }

        // Unused for these tests
        public Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(string code, string redirectUri, string clientId, string codeVerifier, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool ok, object? payload, string? error, int status)> ExchangeRefreshTokenAsync(string refreshToken, string clientId, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool ok, object? payload, string? error, int status)> CreateClientCredentialsTokenAsync(string clientId, string audience, string[] requestedScopes, string issuer, string? dpopJkt = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool ok, object? payload, string? error, int status)> ExchangeTokenAsync(string subjectToken, string? subjectTokenType, string? requestedTokenType, string? requestedAudience, string[] requestedScopes, string callerClientId, string issuer, string? dpopJkt = null, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [TestMethod]
    public async Task Ciba_PollFlow_IssuesTokensAfterAuthorization()
    {
        using var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        // Create client
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "test-client",
            ClientName = "Test Client",
            TenantId = tenantId
        };
        db.Clients.Add(client);

        // Create a user
        var user = new User { Id = Guid.NewGuid(), Name = "Test User", Email = "u@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // CIBA config
        var oidcOpt = new OidcOptions { Issuer = "https://test.example.com" };
        var authOpt = Options.Create(new AuthOptions { EnableCiba = true, CibaPollingIntervalSeconds = 1, CibaAuthRequestLifetimeSeconds = 600 });
        var clientStore = new StubClientStore(client);
        var notification = new StubNotificationService();
        var cibaHandler = new CibaAuthenticationHandler(oidcOpt, authOpt, db, clientStore, new StubClientAssertionValidator(), new TestTenantAccessor(tenantId), notification, NullLogger<CibaAuthenticationHandler>.Instance);

        // Call bc-authorize
        var form = new Dictionary<string, string>
        {
            ["login_hint"] = "testuser",
            ["scope"] = "openid",
        };
        var ctx = CreateHttpContextWithForm("/bc-authorize", form);
        ctx.Request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("test-client:secret"))).ToString();

        var res = await cibaHandler.HandleAsync(ctx);
        await res.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        var json = await new System.IO.StreamReader(ctx.Response.Body).ReadToEndAsync();
        var doc = JsonDocument.Parse(json);
        var authReqId = doc.RootElement.GetProperty("auth_req_id").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(authReqId));

        // Polling: should return authorization_pending
        var tokenHandler = new CibaGrantHandler(db, new StubTokenService(), authOpt, notification, NullLogger<CibaGrantHandler>.Instance);

        var tokenForm = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["grant_type"] = OAuthConstants.GrantTypes.Ciba,
            ["auth_req_id"] = authReqId
        });

        var tokenHttp = new DefaultHttpContext();
        tokenHttp.Request.Scheme = "https";
        tokenHttp.Request.Host = new HostString("test.example.com");
        tokenHttp.Request.Method = "POST";
        tokenHttp.Features.Set<IFormFeature>(new FormFeature(tokenForm));

        var ctxForGrant = new TokenRequestContext(tokenHttp, OAuthConstants.GrantTypes.Ciba, "test-client", tenantId, tokenForm, oidcOpt, new StubTokenService(), null!, null, client, false);

        var result1 = await tokenHandler.TryHandleAsync(ctxForGrant);
        Assert.IsTrue(result1.Handled);
        Assert.IsFalse(result1.Success);
        Assert.IsNotNull(result1.Result);

        // Authorize the entry
        var entry = await db.CibaAuthenticationRequests.FirstOrDefaultAsync(r => r.AuthReqId == authReqId);
        Assert.IsNotNull(entry);
        entry.Status = CibaRequestStatus.Authorized;
        entry.UserId = user.Id;
        // Bypass polling slow-down by setting last polled time to the past
        entry.LastPolledAt = DateTimeOffset.UtcNow.AddSeconds(-(entry.IntervalSeconds + 1));
        await db.SaveChangesAsync();

        // Poll again - should return tokens
        var result2 = await tokenHandler.TryHandleAsync(ctxForGrant);
        Assert.IsTrue(result2.Handled);
        Assert.IsTrue(result2.Success);
        Assert.IsNotNull(result2.Result);
    }

    [TestMethod]
    public async Task Ciba_PingFlow_NotificationCalled()
    {
        using var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "ping-client",
            ClientName = "Ping Client",
            TenantId = tenantId
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var oidcOpt = new OidcOptions { Issuer = "https://test.example.com" };
        var authOpt = Options.Create(new AuthOptions { EnableCiba = true });
        var clientStore = new StubClientStore(client);
        var notification = new StubNotificationService();
        var cibaHandler = new CibaAuthenticationHandler(oidcOpt, authOpt, db, clientStore, new StubClientAssertionValidator(), new TestTenantAccessor(tenantId), notification, NullLogger<CibaAuthenticationHandler>.Instance);

        var form = new Dictionary<string, string>
        {
            ["login_hint"] = "testuser",
            ["scope"] = "openid",
            ["client_notification_token"] = "notify-123"
        };
        var ctx = CreateHttpContextWithForm("/bc-authorize", form);
        ctx.Request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("ping-client:secret"))).ToString();

        var res = await cibaHandler.HandleAsync(ctx);
        await res.ExecuteAsync(ctx);
        Assert.IsTrue(notification.WasCalled);

        // Verify auth_req_id exists and has the client_notification_token
        ctx.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        var json = await new System.IO.StreamReader(ctx.Response.Body).ReadToEndAsync();
        var doc = JsonDocument.Parse(json);
        var authReqId = doc.RootElement.GetProperty("auth_req_id").GetString();
        Assert.IsNotNull(authReqId);

        var entry = await db.CibaAuthenticationRequests.FirstOrDefaultAsync(r => r.AuthReqId == authReqId);
        Assert.IsNotNull(entry);
        Assert.AreEqual("notify-123", entry.ClientNotificationToken);
    }

    [TestMethod]
    public async Task Ciba_PollFlow_ExpiredAuthReq_ReturnsExpiredToken()
    {
        using var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        // Create client and call bc-authorize to create an entry
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "exp-client",
            ClientName = "Exp Client",
            TenantId = tenantId
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var oidcOpt = new OidcOptions { Issuer = "https://test.example.com" };
        var authOpt = Options.Create(new AuthOptions { EnableCiba = true });
        var clientStore = new StubClientStore(client);
        var notification = new StubNotificationService();
        var cibaHandler = new CibaAuthenticationHandler(oidcOpt, authOpt, db, clientStore, new StubClientAssertionValidator(), new TestTenantAccessor(tenantId), notification, NullLogger<CibaAuthenticationHandler>.Instance);

        var form = new Dictionary<string, string>
        {
            ["login_hint"] = "testuser",
            ["scope"] = "openid",
        };
        var ctx = CreateHttpContextWithForm("/bc-authorize", form);
        ctx.Request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("exp-client:secret"))).ToString();

        var res = await cibaHandler.HandleAsync(ctx);
        await res.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        var json = await new System.IO.StreamReader(ctx.Response.Body).ReadToEndAsync();
        var doc = JsonDocument.Parse(json);
        var authReqId = doc.RootElement.GetProperty("auth_req_id").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(authReqId));

        // Force expiry in DB
        var entry = await db.CibaAuthenticationRequests.FirstOrDefaultAsync(r => r.AuthReqId == authReqId);
        Assert.IsNotNull(entry);
        entry.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        // Poll - should return expired_token error
        var tokenHandler = new CibaGrantHandler(db, new StubTokenService(), authOpt, notification, NullLogger<CibaGrantHandler>.Instance);
        var tokenForm = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["grant_type"] = OAuthConstants.GrantTypes.Ciba,
            ["auth_req_id"] = authReqId
        });
        var tokenHttp = new DefaultHttpContext();
        tokenHttp.Request.Scheme = "https";
        tokenHttp.Request.Host = new HostString("test.example.com");
        tokenHttp.Request.Method = "POST";
        tokenHttp.Features.Set<IFormFeature>(new FormFeature(tokenForm));

        var ctxForGrant = new TokenRequestContext(tokenHttp, OAuthConstants.GrantTypes.Ciba, "exp-client", tenantId, tokenForm, oidcOpt, new StubTokenService(), null!, null, client, false);

        var result = await tokenHandler.TryHandleAsync(ctxForGrant);
        Assert.IsTrue(result.Handled);
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Result);

        // Execute result and verify error code
        var outHttp = new DefaultHttpContext();
        outHttp.Response.Body = new MemoryStream();
        // Provide minimal request services required by ASP.NET Core result execution
        var spServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        spServices.AddLogging();
        spServices.AddOptions();
        outHttp.RequestServices = spServices.BuildServiceProvider();
        await result.Result.ExecuteAsync(outHttp);
        outHttp.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        var outJson = await new System.IO.StreamReader(outHttp.Response.Body).ReadToEndAsync();
        var outDoc = JsonDocument.Parse(outJson);
        var error = outDoc.RootElement.GetProperty("error").GetString();
        Assert.AreEqual("expired_token", error);
    }

    [TestMethod]
    public async Task Ciba_Request_RequestedExpiry_IsStoredAndReturned()
    {
        using var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "reqexp-client",
            ClientName = "ReqExp Client",
            TenantId = tenantId
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var oidcOpt = new OidcOptions { Issuer = "https://test.example.com" };
        var authOpt = Options.Create(new AuthOptions { EnableCiba = true, CibaAuthRequestLifetimeSeconds = 600 });
        var clientStore = new StubClientStore(client);
        var notification = new StubNotificationService();
        var cibaHandler = new CibaAuthenticationHandler(oidcOpt, authOpt, db, clientStore, new StubClientAssertionValidator(), new TestTenantAccessor(tenantId), notification, NullLogger<CibaAuthenticationHandler>.Instance);

        var form = new Dictionary<string, string>
        {
            ["login_hint"] = "testuser",
            ["scope"] = "openid",
            ["requested_expiry"] = "5"
        };
        var ctx = CreateHttpContextWithForm("/bc-authorize", form);
        ctx.Request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("reqexp-client:secret"))).ToString();

        var res = await cibaHandler.HandleAsync(ctx);
        await res.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        var json = await new System.IO.StreamReader(ctx.Response.Body).ReadToEndAsync();
        var doc = JsonDocument.Parse(json);
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
        Assert.AreEqual(5, expiresIn);

        var authReqId = doc.RootElement.GetProperty("auth_req_id").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(authReqId));

        var entry = await db.CibaAuthenticationRequests.FirstOrDefaultAsync(r => r.AuthReqId == authReqId);
        Assert.IsNotNull(entry);
        Assert.AreEqual(5, entry.RequestedExpiresIn);
    }

    [TestMethod]
    public async Task Ciba_PingFlow_NotificationFailure_DoesNotFailRequest()
    {
        using var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "ping-client-err",
            ClientName = "Ping Client Err",
            TenantId = tenantId
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var oidcOpt = new OidcOptions { Issuer = "https://test.example.com" };
        var authOpt = Options.Create(new AuthOptions { EnableCiba = true });
        var clientStore = new StubClientStore(client);
        var notification = new ThrowingNotificationService();
        var cibaHandler = new CibaAuthenticationHandler(oidcOpt, authOpt, db, clientStore, new StubClientAssertionValidator(), new TestTenantAccessor(tenantId), notification, NullLogger<CibaAuthenticationHandler>.Instance);

        var form = new Dictionary<string, string>
        {
            ["login_hint"] = "testuser",
            ["scope"] = "openid",
            ["client_notification_token"] = "notify-err"
        };
        var ctx = CreateHttpContextWithForm("/bc-authorize", form);
        ctx.Request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("ping-client-err:secret"))).ToString();

        var res = await cibaHandler.HandleAsync(ctx);
        await res.ExecuteAsync(ctx);
        Assert.IsTrue(notification.WasCalled);

        ctx.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        var json = await new System.IO.StreamReader(ctx.Response.Body).ReadToEndAsync();
        var doc = JsonDocument.Parse(json);
        var authReqId = doc.RootElement.GetProperty("auth_req_id").GetString();
        Assert.IsNotNull(authReqId);

        var entry = await db.CibaAuthenticationRequests.FirstOrDefaultAsync(r => r.AuthReqId == authReqId);
        Assert.IsNotNull(entry);
    }

    private sealed class ThrowingNotificationService : ICibaNotificationService
    {
        public bool WasCalled;
        public Task NotifyUserAsync(CibaAuthenticationRequest request, CancellationToken ct = default)
        {
            WasCalled = true;
            throw new Exception("simulated-notify-failure");
        }
        public Task SendPingNotificationAsync(CibaAuthenticationRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new Exception("simulated-notify-failure");
        }
    }

    private sealed class StubClientAssertionValidator : IClientAssertionValidator
    {
        private readonly bool _valid;
        public StubClientAssertionValidator(bool valid = true) { _valid = valid; }
        public Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default) => Task.FromResult(_valid);
    }

    // Minimal tenant accessor for tests
    private sealed class TestTenantAccessor : ITenantAccessor
    {
        public TestTenantAccessor(Guid id) { CurrentTenant = new TenantContext { TenantId = id, Slug = "test", Name = "Test" }; }
        public TenantContext? CurrentTenant { get; private set; }
        public void SetTenant(TenantContext? tenant) { CurrentTenant = tenant; }
    }
}
