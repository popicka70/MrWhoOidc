using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;
using System.Text;
using System.Text.Json;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class DeviceAuthorizationTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static DeviceAuthorizationHandler CreateHandler(
        AuthDbContext? db = null,
        IClientStore? clients = null,
        IClientAssertionValidator? assertions = null,
        IOptions<AuthOptions>? authOptions = null,
        IOptions<OidcOptions>? oidcOptions = null)
    {
        var logger = NullLogger<DeviceAuthorizationHandler>.Instance;

        db ??= CreateDb();
        clients ??= new StubClientStore();
        assertions ??= new StubClientAssertionValidator();
        authOptions ??= Options.Create(new AuthOptions
        {
            EnableDeviceAuthorizationGrant = true,
            DeviceCodeLifetimeSeconds = 600,
            DeviceCodePollingIntervalSeconds = 5,
            DeviceCodeUserCodeLength = 8,
            DeviceCodeUserCodeCharset = "BCDFGHJKLMNPQRSTVWXZ"
        });
        oidcOptions ??= Options.Create(new OidcOptions { Issuer = "https://test.example.com" });

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();

        return new DeviceAuthorizationHandler(oidcOptions.Value, authOptions, db, clients, assertions, tenantAccessor, logger);
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
        context.Request.Path = "/device/authorize";
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

    [TestMethod]
    public async Task HandleAsync_MissingClientId_ReturnsInvalidRequest()
    {
        var db = CreateDb();
        var handler = CreateHandler(db);
        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["scope"] = "openid profile"
        });

        var result = await handler.HandleAsync(ctx);

        // Execute the result to write to the response body
        await result.ExecuteAsync(ctx);

        // Verify error response
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var json = JsonDocument.Parse(body);

        Assert.AreEqual("invalid_request", json.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task HandleAsync_UnknownClient_ReturnsInvalidClient()
    {
        var db = CreateDb();
        var handler = CreateHandler(db);
        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "unknown-client",
            ["scope"] = "openid profile"
        });

        var result = await handler.HandleAsync(ctx);

        // Execute the result to write to the response body
        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var json = JsonDocument.Parse(body);

        Assert.AreEqual("invalid_client", json.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task HandleAsync_ValidRequest_ReturnsDeviceCode()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        // Add test realm
        db.Realms.Add(new Realm
        {
            Id = realmId,
            TenantId = tenantId,
            Name = "test-realm"
        });

        // Add test client (public client for device flow)
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "device-client",
            ClientName = "Test Device Client",
            RealmId = realmId,
            RequirePkce = false // Device flow doesn't use PKCE
        });
        await db.SaveChangesAsync();

        // Create handler with matching tenant accessor
        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext { TenantId = tenantId, Slug = "test" });

        var authOptions = Options.Create(new AuthOptions
        {
            EnableDeviceAuthorizationGrant = true,
            DeviceCodeLifetimeSeconds = 600,
            DeviceCodePollingIntervalSeconds = 5
        });
        var oidcOptions = Options.Create(new OidcOptions { Issuer = "https://test.example.com" });
        var logger = NullLogger<DeviceAuthorizationHandler>.Instance;

        var handler = new DeviceAuthorizationHandler(
            oidcOptions.Value, authOptions, db,
            new StubClientStore(), new StubClientAssertionValidator(),
            tenantAccessor, logger);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "device-client",
            ["scope"] = "openid profile"
        });

        var result = await handler.HandleAsync(ctx);

        // Execute the result to write to the response body
        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var json = JsonDocument.Parse(body);

        // Verify response contains required fields per RFC 8628
        Assert.IsTrue(json.RootElement.TryGetProperty("device_code", out var deviceCode));
        Assert.IsFalse(string.IsNullOrEmpty(deviceCode.GetString()));

        Assert.IsTrue(json.RootElement.TryGetProperty("user_code", out var userCode));
        Assert.IsFalse(string.IsNullOrEmpty(userCode.GetString()));

        Assert.IsTrue(json.RootElement.TryGetProperty("verification_uri", out var verificationUri));
        Assert.AreEqual("https://test.example.com/device", verificationUri.GetString());

        Assert.IsTrue(json.RootElement.TryGetProperty("verification_uri_complete", out _));

        Assert.IsTrue(json.RootElement.TryGetProperty("expires_in", out var expiresIn));
        Assert.AreEqual(600, expiresIn.GetInt32());

        Assert.IsTrue(json.RootElement.TryGetProperty("interval", out var interval));
        Assert.AreEqual(5, interval.GetInt32());

        // Verify device code was persisted
        var storedEntry = await db.DeviceCodes.FirstOrDefaultAsync();
        Assert.IsNotNull(storedEntry);
        Assert.AreEqual("device-client", storedEntry.ClientId);
        Assert.AreEqual(DeviceCodeStatus.Pending, storedEntry.Status);
    }

    [TestMethod]
    public async Task HandleAsync_UserCodeFormat_IsCorrect()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "test-realm" });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = "device-client",
            RealmId = realmId
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext { TenantId = tenantId, Slug = "test" });

        var authOptions = Options.Create(new AuthOptions
        {
            EnableDeviceAuthorizationGrant = true,
            DeviceCodeUserCodeLength = 8,
            DeviceCodeUserCodeCharset = "BCDFGHJKLMNPQRSTVWXZ"
        });
        var oidcOptions = Options.Create(new OidcOptions { Issuer = "https://test.example.com" });
        var logger = NullLogger<DeviceAuthorizationHandler>.Instance;

        var handler = new DeviceAuthorizationHandler(
            oidcOptions.Value, authOptions, db,
            new StubClientStore(), new StubClientAssertionValidator(),
            tenantAccessor, logger);

        var ctx = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "device-client"
        });

        var result = await handler.HandleAsync(ctx);

        // Execute the result to write to the response body
        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var json = JsonDocument.Parse(body);

        var userCode = json.RootElement.GetProperty("user_code").GetString()!;

        // Should be formatted as XXXX-XXXX
        Assert.AreEqual(9, userCode.Length); // 8 chars + 1 hyphen
        Assert.IsTrue(userCode.Contains('-'));

        // Characters should be uppercase consonants only
        var codeWithoutHyphen = userCode.Replace("-", "");
        foreach (var c in codeWithoutHyphen)
        {
            Assert.IsTrue("BCDFGHJKLMNPQRSTVWXZ".Contains(c),
                $"User code contains invalid character: {c}");
        }
    }

    // Stub implementations for testing
    private sealed class StubClientStore : IClientStore
    {
        public Task<MrWhoOidc.Auth.Persistence.Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default) => Task.FromResult<MrWhoOidc.Auth.Persistence.Client?>(null);
        public Task<bool> ValidateClientSecretAsync(string clientId, string? secret, CancellationToken ct = default) => Task.FromResult(true);
        public IQueryable<MrWhoOidc.Auth.Persistence.Client> QueryClients(CancellationToken ct = default) => Enumerable.Empty<MrWhoOidc.Auth.Persistence.Client>().AsQueryable();
        public Task InvalidateClientCacheAsync(string clientId, Guid tenantId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClientSecret?> GetPrimarySecretAsync(Guid clientRecordId, CancellationToken ct = default) => Task.FromResult<ClientSecret?>(null);
        public Task<List<ClientSecret>> GetActiveSecretsAsync(Guid clientRecordId, CancellationToken ct = default) => Task.FromResult(new List<ClientSecret>());
        public Task<ClientSecret> CreateSecretAsync(Guid clientRecordId, string secretValue, string? description, string? createdBy, DateTime? expiresAtUtc = null, CancellationToken ct = default) => Task.FromResult(new ClientSecret());
        public Task<bool> ActivateSecretAsync(Guid secretId, string activatedBy, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetPrimarySecretAsync(Guid secretId, string updatedBy, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RevokeSecretAsync(Guid secretId, string revokedBy, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RecordSecretUsageAsync(Guid secretId, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class StubClientAssertionValidator : IClientAssertionValidator
    {
        public Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default) => Task.FromResult(true);
    }
}
