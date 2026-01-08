using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.UnitTests.TestSupport;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Models.DynamicRegistration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Tests for RFC 7591 (Dynamic Client Registration) and RFC 7592 (Client Configuration Management)
/// </summary>
[TestClass]
public sealed class DynamicClientRegistrationTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static IServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddScoped<ITenantAccessor, TenantAccessor>();
        services.AddSingleton<IMultiTenancyOptions>(new MultiTenancyOptions());
        services.AddScoped<IIssuerBuilder, IssuerBuilder>();
        return services.BuildServiceProvider();
    }

    private static (RegistrationHandler handler, TenantAccessor tenantAccessor) CreateRegistrationHandler(
        AuthDbContext? db = null,
        IOptions<AuthOptions>? authOptions = null)
    {
        db ??= CreateDb();
        authOptions ??= Options.Create(new AuthOptions
        {
            EnableDynamicClientRegistration = true
        });

        var tenantAccessor = new TenantAccessor();
        var logger = NullLogger<RegistrationHandler>.Instance;

        var handler = new RegistrationHandler(db, tenantAccessor, authOptions, logger);
        return (handler, tenantAccessor);
    }

    private static (ClientConfigurationHandler handler, TenantAccessor tenantAccessor) CreateConfigurationHandler(
        AuthDbContext? db = null,
        IOptions<AuthOptions>? authOptions = null)
    {
        db ??= CreateDb();
        authOptions ??= Options.Create(new AuthOptions
        {
            EnableDynamicClientRegistration = true,
            EnableClientConfigurationEndpoint = true
        });

        var tenantAccessor = new TenantAccessor();
        var logger = NullLogger<ClientConfigurationHandler>.Instance;

        var handler = new ClientConfigurationHandler(db, tenantAccessor, authOptions, logger);
        return (handler, tenantAccessor);
    }

    private static DefaultHttpContext CreateHttpContext(
        string method = "POST",
        string path = "/register",
        string? body = null,
        string? authorizationHeader = null)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = CreateServiceProvider();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Request.Path = path;
        context.Request.Method = method;
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();

        if (authorizationHeader != null)
        {
            context.Request.Headers.Authorization = authorizationHeader;
        }

        if (body != null)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bodyBytes);
            context.Request.ContentLength = bodyBytes.Length;
        }

        return context;
    }

    private static async Task<T?> GetResponseBody<T>(HttpContext context) where T : class
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var json = await reader.ReadToEndAsync();
        if (string.IsNullOrEmpty(json)) return null;
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static void SetTenant(TenantAccessor accessor, Guid tenantId, string slug = "test")
    {
        accessor.SetTenant(new TenantContext
        {
            TenantId = tenantId,
            Slug = slug,
            Name = "Test Tenant"
        });
    }

    private static async Task<Guid> CreateTestTenant(AuthDbContext db, string slug = "default")
    {
        var tenantId = GuidHelper.NewId();
        
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Test Tenant",
            Slug = slug,
            Status = TenantStatus.Active
        };
        db.Tenants.Add(tenant);

        // Create default realm (required for client registration)
        var realm = new Realm
        {
            Id = GuidHelper.NewId(),
            TenantId = tenantId,
            Name = "default",
            DisplayName = "Default Realm",
            AllowUnconfirmedLogin = true
        };
        db.Realms.Add(realm);

        // Enable dynamic registration for this tenant by selecting a realm.
        tenant.SettingsJson = JsonSerializer.Serialize(
            new TenantSettings
            {
                Auth = new AuthTenantSettings
                {
                    DynamicClientRegistrationRealmId = realm.Id
                }
            },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

        // Create a signing key (required for token operations)
        var signingKey = new SigningKey
        {
            Id = GuidHelper.NewId(),
            TenantId = tenantId,
            Kid = $"test-key-{Guid.NewGuid():N}",
            Use = "sig",
            Alg = "RS256",
            JwkJson = GenerateTestRsaJwk(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SigningKeys.Add(signingKey);

        await db.SaveChangesAsync();
        return tenantId;
    }

    // Cached JWK JSON to avoid generating RSA keys per test
    private static readonly Lazy<string> s_testRsaJwk = new(
        () => GenerateTestRsaJwkInternal(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static string GenerateTestRsaJwk() => s_testRsaJwk.Value;

    private static string GenerateTestRsaJwkInternal()
    {
        // Use shared RSA key instead of generating a new one
        var parameters = SharedTestKeys.GetRsaParameters(includePrivate: true);
        
        return JsonSerializer.Serialize(new
        {
            kty = "RSA",
            n = Convert.ToBase64String(parameters.Modulus!),
            e = Convert.ToBase64String(parameters.Exponent!),
            d = Convert.ToBase64String(parameters.D!),
            p = Convert.ToBase64String(parameters.P!),
            q = Convert.ToBase64String(parameters.Q!),
            dp = Convert.ToBase64String(parameters.DP!),
            dq = Convert.ToBase64String(parameters.DQ!),
            qi = Convert.ToBase64String(parameters.InverseQ!)
        });
    }

    #region RFC 7591 - POST /register Tests

    [TestMethod]
    public async Task Register_WhenFeatureDisabled_Returns400()
    {
        var authOptions = Options.Create(new AuthOptions
        {
            EnableDynamicClientRegistration = false
        });
        var (handler, _) = CreateRegistrationHandler(authOptions: authOptions);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"]
        };
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(400, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_MissingRedirectUris_Returns400()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        var (handler, tenantAccessor) = CreateRegistrationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var request = new ClientRegistrationRequest
        {
            ClientName = "Test Client"
        };
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(400, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_InvalidRedirectUri_Returns400()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        var (handler, tenantAccessor) = CreateRegistrationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["not-a-valid-uri"]
        };
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(400, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_HttpRedirectUri_NonLocalhost_Returns400()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        var (handler, tenantAccessor) = CreateRegistrationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["http://external.example.com/callback"]
        };
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(400, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_HttpRedirectUri_Localhost_IsAllowed()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        var (handler, tenantAccessor) = CreateRegistrationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["http://localhost:8080/callback"]
        };
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        // Should be 201 Created for successful registration
        Assert.AreEqual(201, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_ValidRequest_Returns201WithClientCredentials()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        var (handler, tenantAccessor) = CreateRegistrationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"],
            ClientName = "Test Client",
            GrantTypes = ["authorization_code", "refresh_token"],
            ResponseTypes = ["code"]
        };
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(201, ctx.Response.StatusCode);

        var response = await GetResponseBody<ClientRegistrationResponse>(ctx);
        Assert.IsNotNull(response);
        Assert.IsNotNull(response.ClientId);
        Assert.IsTrue(response.ClientId.StartsWith("dyn_"));
        Assert.IsNotNull(response.ClientSecret);
        Assert.IsNotNull(response.RegistrationAccessToken);
        Assert.IsNotNull(response.RegistrationClientUri);
    }

    [TestMethod]
    public async Task Register_WithInitialAccessTokenRequired_NoToken_Returns401()
    {
        var authOptions = Options.Create(new AuthOptions
        {
            EnableDynamicClientRegistration = true,
            RequireInitialAccessToken = true,
            InitialAccessTokenHashes = [Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("valid-token")))]
        });
        var (handler, _) = CreateRegistrationHandler(authOptions: authOptions);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"]
        };
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(401, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_WithInitialAccessTokenRequired_InvalidToken_Returns401()
    {
        var authOptions = Options.Create(new AuthOptions
        {
            EnableDynamicClientRegistration = true,
            RequireInitialAccessToken = true,
            InitialAccessTokenHashes = [Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("valid-token")))]
        });
        var (handler, _) = CreateRegistrationHandler(authOptions: authOptions);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"]
        };
        var ctx = CreateHttpContext(
            body: JsonSerializer.Serialize(request),
            authorizationHeader: "Bearer invalid-token");

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(401, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_WithInitialAccessTokenRequired_ValidToken_Succeeds()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var validToken = "valid-initial-access-token";
        var authOptions = Options.Create(new AuthOptions
        {
            EnableDynamicClientRegistration = true,
            RequireInitialAccessToken = true,
            InitialAccessTokenHashes = [Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(validToken)))]
        });
        var (handler, tenantAccessor) = CreateRegistrationHandler(db, authOptions);
        SetTenant(tenantAccessor, tenantId);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"]
        };
        var ctx = CreateHttpContext(
            body: JsonSerializer.Serialize(request),
            authorizationHeader: $"Bearer {validToken}");

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(201, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_InvalidGrantType_Returns400()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        var (handler, tenantAccessor) = CreateRegistrationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"],
            GrantTypes = ["unsupported_grant_type"]
        };
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(400, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_InvalidResponseType_Returns400()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        var (handler, tenantAccessor) = CreateRegistrationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"],
            ResponseTypes = ["unsupported_response_type"]
        };
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(400, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_HybridResponseType_Returns400()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        var (handler, tenantAccessor) = CreateRegistrationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"],
            ResponseTypes = ["code id_token"]
        };
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(400, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task Register_PublicClient_NoSecret_Succeeds()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        var (handler, tenantAccessor) = CreateRegistrationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var request = new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"],
            TokenEndpointAuthMethod = "none" // Public client
        };
        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));

        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(201, ctx.Response.StatusCode);

        var response = await GetResponseBody<ClientRegistrationResponse>(ctx);
        Assert.IsNotNull(response);
        Assert.IsNull(response.ClientSecret); // Public clients don't get a secret
    }

    #endregion

    #region RFC 7592 - GET/PUT/DELETE /register/{client_id} Tests

    [TestMethod]
    public async Task GetClient_WhenFeatureDisabled_Returns400()
    {
        var authOptions = Options.Create(new AuthOptions
        {
            EnableDynamicClientRegistration = false
        });
        var (handler, _) = CreateConfigurationHandler(authOptions: authOptions);

        var ctx = CreateHttpContext(method: "GET", path: "/register/test-client");

        var result = await handler.GetClientAsync(ctx, "test-client");
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(400, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task GetClient_WhenConfigEndpointDisabled_Returns400()
    {
        var authOptions = Options.Create(new AuthOptions
        {
            EnableDynamicClientRegistration = true,
            EnableClientConfigurationEndpoint = false
        });
        var (handler, _) = CreateConfigurationHandler(authOptions: authOptions);

        var ctx = CreateHttpContext(method: "GET", path: "/register/test-client");

        var result = await handler.GetClientAsync(ctx, "test-client");
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(400, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task GetClient_MissingAuthorizationHeader_Returns401()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var client = new Auth.Persistence.Client
        {
            Id = GuidHelper.NewId(),
            ClientId = "dyn_test123",
            ClientName = "Test Client",
            TenantId = tenantId
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var (handler, tenantAccessor) = CreateConfigurationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var ctx = CreateHttpContext(method: "GET", path: "/register/dyn_test123");

        var result = await handler.GetClientAsync(ctx, "dyn_test123");
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(401, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task GetClient_InvalidRegistrationAccessToken_Returns401()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var client = new Auth.Persistence.Client
        {
            Id = GuidHelper.NewId(),
            ClientId = "dyn_test123",
            ClientName = "Test Client",
            TenantId = tenantId
        };
        db.Clients.Add(client);

        // Store a valid token hash
        var validToken = "valid-reg-access-token";
        var tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(validToken)));
        db.DynamicRegistrationTokens.Add(new DynamicRegistrationToken
        {
            Id = Guid.NewGuid().ToString(),
            ClientId = "dyn_test123",
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var (handler, tenantAccessor) = CreateConfigurationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var ctx = CreateHttpContext(
            method: "GET",
            path: "/register/dyn_test123",
            authorizationHeader: "Bearer wrong-token");

        var result = await handler.GetClientAsync(ctx, "dyn_test123");
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(401, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task GetClient_ValidToken_ReturnsClientMetadata()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var client = new Auth.Persistence.Client
        {
            Id = GuidHelper.NewId(),
            ClientId = "dyn_test123",
            ClientName = "Test Client",
            TenantId = tenantId,
            AllowedLoginRedirectUrisJson = "[\"https://client.example.com/callback\"]"
        };
        db.Clients.Add(client);

        var validToken = "valid-reg-access-token";
        var tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(validToken)));
        db.DynamicRegistrationTokens.Add(new DynamicRegistrationToken
        {
            Id = Guid.NewGuid().ToString(),
            ClientId = "dyn_test123",
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var (handler, tenantAccessor) = CreateConfigurationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var ctx = CreateHttpContext(
            method: "GET",
            path: "/register/dyn_test123",
            authorizationHeader: $"Bearer {validToken}");

        var result = await handler.GetClientAsync(ctx, "dyn_test123");
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(200, ctx.Response.StatusCode);

        var response = await GetResponseBody<ClientRegistrationResponse>(ctx);
        Assert.IsNotNull(response);
        Assert.AreEqual("dyn_test123", response.ClientId);
        Assert.AreEqual("Test Client", response.ClientName);
        Assert.IsNull(response.ClientSecret); // Should not return secret on GET
        Assert.IsNull(response.RegistrationAccessToken); // Should not return RAT on GET
    }

    [TestMethod]
    public async Task DeleteClient_ValidToken_RemovesClient()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var client = new Auth.Persistence.Client
        {
            Id = GuidHelper.NewId(),
            ClientId = "dyn_test123",
            ClientName = "Test Client",
            TenantId = tenantId
        };
        db.Clients.Add(client);

        var validToken = "valid-reg-access-token";
        var tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(validToken)));
        db.DynamicRegistrationTokens.Add(new DynamicRegistrationToken
        {
            Id = Guid.NewGuid().ToString(),
            ClientId = "dyn_test123",
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var (handler, tenantAccessor) = CreateConfigurationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var ctx = CreateHttpContext(
            method: "DELETE",
            path: "/register/dyn_test123",
            authorizationHeader: $"Bearer {validToken}");

        var result = await handler.DeleteClientAsync(ctx, "dyn_test123");
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(204, ctx.Response.StatusCode);

        // Verify client was deleted
        var deletedClient = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == "dyn_test123");
        Assert.IsNull(deletedClient);
    }

    [TestMethod]
    public async Task UpdateClient_ValidToken_UpdatesMetadata()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var client = new Auth.Persistence.Client
        {
            Id = GuidHelper.NewId(),
            ClientId = "dyn_test123",
            ClientName = "Test Client",
            TenantId = tenantId,
            AllowedLoginRedirectUrisJson = "[\"https://client.example.com/callback\"]"
        };
        db.Clients.Add(client);

        var validToken = "valid-reg-access-token";
        var tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(validToken)));
        db.DynamicRegistrationTokens.Add(new DynamicRegistrationToken
        {
            Id = Guid.NewGuid().ToString(),
            ClientId = "dyn_test123",
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var (handler, tenantAccessor) = CreateConfigurationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var updateRequest = new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback", "https://client.example.com/callback2"],
            ClientName = "Updated Client Name"
        };
        var ctx = CreateHttpContext(
            method: "PUT",
            path: "/register/dyn_test123",
            body: JsonSerializer.Serialize(updateRequest),
            authorizationHeader: $"Bearer {validToken}");

        var result = await handler.UpdateClientAsync(ctx, "dyn_test123");
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(200, ctx.Response.StatusCode);

        // Verify client was updated
        var updatedClient = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == "dyn_test123");
        Assert.IsNotNull(updatedClient);
        Assert.AreEqual("Updated Client Name", updatedClient.ClientName);
    }

    [TestMethod]
    public async Task GetClient_ExpiredToken_Returns401()
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var client = new Auth.Persistence.Client
        {
            Id = GuidHelper.NewId(),
            ClientId = "dyn_test123",
            ClientName = "Test Client",
            TenantId = tenantId
        };
        db.Clients.Add(client);

        var validToken = "valid-reg-access-token";
        var tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(validToken)));
        db.DynamicRegistrationTokens.Add(new DynamicRegistrationToken
        {
            Id = Guid.NewGuid().ToString(),
            ClientId = "dyn_test123",
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            ExpiresAt = DateTime.UtcNow.AddDays(-1) // Expired
        });
        await db.SaveChangesAsync();

        var (handler, tenantAccessor) = CreateConfigurationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var ctx = CreateHttpContext(
            method: "GET",
            path: "/register/dyn_test123",
            authorizationHeader: $"Bearer {validToken}");

        var result = await handler.GetClientAsync(ctx, "dyn_test123");
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(401, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task GetClient_ClientNotFound_Returns401()
    {
        // Note: Returns 401 (not 404) to prevent client enumeration attacks
        // The handler validates the token before revealing client existence
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);

        var (handler, tenantAccessor) = CreateConfigurationHandler(db);
        SetTenant(tenantAccessor, tenantId);

        var ctx = CreateHttpContext(
            method: "GET",
            path: "/register/nonexistent",
            authorizationHeader: "Bearer some-token");

        var result = await handler.GetClientAsync(ctx, "nonexistent");
        await result.ExecuteAsync(ctx);

        Assert.AreEqual(401, ctx.Response.StatusCode);
    }

    #endregion
}
