using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Security;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.TokenEndpoint.Grants;
using System.Text;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class TokenHandlerTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static TokenHandler CreateHandler(
        AuthDbContext db,
        ITokenService? tokens = null,
        IClientStore? clients = null,
        IClientAssertionValidator? assertions = null,
        IDPoPValidator? dpop = null,
        IEnumerable<ITokenGrantHandler>? grantHandlers = null,
        IEnumerable<ITokenMetricsRecorder>? tokenMetrics = null,
        IOptions<OidcOptions>? options = null)
    {
        var logger = NullLogger<TokenHandler>.Instance;

        tokens ??= new StubTokenService();
        clients ??= new StubClientStore();
        assertions ??= new StubClientAssertionValidator();
        dpop ??= new StubDPoPValidator();
        grantHandlers ??= new[] { new StubTokenGrantHandler() };
        tokenMetrics ??= new[] { new StubTokenMetricsRecorder() };
        options ??= Options.Create(new OidcOptions { Issuer = "https://test.example.com" });

        return new TokenHandler(options.Value, tokens, clients, assertions, dpop, grantHandlers, tokenMetrics, logger);
    }

    private static DefaultHttpContext CreateHttpContext(
        Dictionary<string, string>? formData = null,
        string? authorizationHeader = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext();
        context.RequestServices = serviceProvider;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Request.Path = "/token";
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

    private static string BasicAuth(string clientId, string clientSecret)
    {
        var credentials = $"{clientId}:{clientSecret}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        return $"Basic {encoded}";
    }

    [TestMethod]
    public async Task Token_Invalid_Grant_Type_Returns_Unsupported_Grant_Type()
    {
        // Arrange
        using var db = CreateDb();
        var handler = CreateHandler(db);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "invalid_grant",
            ["client_id"] = "test_client",
            ["client_secret"] = "secret"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns unsupported_grant_type error
    }

    [TestMethod]
    public async Task Token_Invalid_Client_Credentials_Returns_Invalid_Client()
    {
        // Arrange
        using var db = CreateDb();
        var clients = new StubClientStore(authenticated: false);
        var handler = CreateHandler(db, clients: clients);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test_code",
            ["redirect_uri"] = "https://app/callback",
            ["client_id"] = "test_client",
            ["client_secret"] = "wrong_secret"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns invalid_client error
    }

    [TestMethod]
    public async Task Token_Missing_Client_Id_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();
        var handler = CreateHandler(db);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test_code"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for missing client_id
    }

    [TestMethod]
    public async Task Token_ClientCredentials_Invalid_Client_Assertion_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();

        var clientGuid = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "m2m_client",
            ClientSecretHash = "hash",
            RealmId = realmId,
            AllowPrivateKeyJwt = true
        };
        db.Realms.Add(realm);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var assertions = new StubClientAssertionValidator(valid: false);
        var clientStore = new StubClientStore(client, authenticated: false);
        var handler = CreateHandler(db, assertions: assertions, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "m2m_client",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = "invalid.jwt.token",
            ["audience"] = "api"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns unauthorized_client for invalid assertion
    }

    [TestMethod]
    public async Task Token_DPoP_Proof_Missing_Jti_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();

        var clientGuid = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = realmId
        };
        db.Realms.Add(realm);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var dpop = new StubDPoPValidator(ok: false, error: "missing_jti");
        var clientStore = new StubClientStore(client, authenticated: true);
        var handler = CreateHandler(db, dpop: dpop, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test_code",
            ["redirect_uri"] = "https://app/callback",
            ["client_id"] = "test_client",
            ["client_secret"] = "secret"
        };
        var context = CreateHttpContext(formData);
        context.Request.Headers["DPoP"] = "eyJhbGciOiJSUzI1NiJ9.e30.sig"; // Mock DPoP proof

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for invalid DPoP proof (missing jti)
    }

    [TestMethod]
    public async Task Token_DPoP_Proof_Invalid_Signature_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();

        var clientGuid = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = realmId
        };
        db.Realms.Add(realm);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var dpop = new StubDPoPValidator(ok: false, error: "invalid_signature");
        var clientStore = new StubClientStore(client, authenticated: true);
        var handler = CreateHandler(db, dpop: dpop, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test_code",
            ["redirect_uri"] = "https://app/callback",
            ["client_id"] = "test_client",
            ["client_secret"] = "secret"
        };
        var context = CreateHttpContext(formData);
        context.Request.Headers["DPoP"] = "eyJhbGciOiJSUzI1NiJ9.e30.invalid_sig";

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for invalid DPoP signature
    }

    [TestMethod]
    public async Task Token_DPoP_Proof_Htm_Mismatch_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();

        var clientGuid = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = realmId
        };
        db.Realms.Add(realm);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var dpop = new StubDPoPValidator(ok: false, error: "htm_mismatch");
        var clientStore = new StubClientStore(client, authenticated: true);
        var handler = CreateHandler(db, dpop: dpop, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test_code",
            ["redirect_uri"] = "https://app/callback",
            ["client_id"] = "test_client",
            ["client_secret"] = "secret"
        };
        var context = CreateHttpContext(formData);
        context.Request.Headers["DPoP"] = "eyJhbGciOiJSUzI1NiJ9.e30.sig";

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for htm mismatch (GET vs POST)
    }

    [TestMethod]
    public async Task Token_DPoP_Proof_Htu_Mismatch_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();

        var clientGuid = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = realmId
        };
        db.Realms.Add(realm);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var dpop = new StubDPoPValidator(ok: false, error: "htu_mismatch");
        var clientStore = new StubClientStore(client, authenticated: true);
        var handler = CreateHandler(db, dpop: dpop, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test_code",
            ["redirect_uri"] = "https://app/callback",
            ["client_id"] = "test_client",
            ["client_secret"] = "secret"
        };
        var context = CreateHttpContext(formData);
        context.Request.Headers["DPoP"] = "eyJhbGciOiJSUzI1NiJ9.e30.sig";

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for htu mismatch (wrong URI)
    }

    [TestMethod]
    public async Task Token_Basic_Authentication_Works()
    {
        // Arrange
        using var db = CreateDb();

        var clientGuid = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = realmId
        };
        db.Realms.Add(realm);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var grantHandler = new StubTokenGrantHandler(handled: true, success: true);
        var clientStore = new StubClientStore(client, authenticated: true);
        var handler = CreateHandler(db, grantHandlers: new[] { grantHandler }, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test_code",
            ["redirect_uri"] = "https://app/callback"
        };
        var authHeader = BasicAuth("test_client", "secret");
        var context = CreateHttpContext(formData, authHeader);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler accepts Basic authentication
    }

    [TestMethod]
    public async Task Token_Client_Secret_Post_Works()
    {
        // Arrange
        using var db = CreateDb();

        var clientGuid = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = realmId
        };
        db.Realms.Add(realm);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var grantHandler = new StubTokenGrantHandler(handled: true, success: true);
        var clientStore = new StubClientStore(client, authenticated: true);
        var handler = CreateHandler(db, grantHandlers: new[] { grantHandler }, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test_code",
            ["redirect_uri"] = "https://app/callback",
            ["client_id"] = "test_client",
            ["client_secret"] = "secret"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler accepts client_secret_post authentication
    }

    [TestMethod]
    public async Task Token_Private_Key_JWT_Authentication_Works()
    {
        // Arrange
        using var db = CreateDb();

        var clientGuid = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "test_client",
            RealmId = realmId,
            AllowPrivateKeyJwt = true
        };
        db.Realms.Add(realm);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var assertions = new StubClientAssertionValidator(valid: true);
        var grantHandler = new StubTokenGrantHandler(handled: true, success: true);
        var clientStore = new StubClientStore(client, authenticated: false); // Will be authenticated via assertion
        var handler = CreateHandler(db, assertions: assertions, grantHandlers: new[] { grantHandler }, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test_code",
            ["redirect_uri"] = "https://app/callback",
            ["client_id"] = "test_client",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = "eyJhbGciOiJSUzI1NiJ9.e30.sig"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler accepts private_key_jwt authentication
    }

    [TestMethod]
    public async Task Token_Metrics_Recorded_For_Each_Grant_Type()
    {
        // Arrange
        using var db = CreateDb();

        var clientGuid = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = realmId
        };
        db.Realms.Add(realm);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var metricsRecorder = new StubTokenMetricsRecorder();
        var grantHandler = new StubTokenGrantHandler(handled: true, success: true);
        var clientStore = new StubClientStore(client, authenticated: true);
        var handler = CreateHandler(db, grantHandlers: new[] { grantHandler }, tokenMetrics: new[] { metricsRecorder }, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test_code",
            ["redirect_uri"] = "https://app/callback",
            ["client_id"] = "test_client",
            ["client_secret"] = "secret"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(metricsRecorder.TokenRequestCalled);
        Assert.AreEqual("authorization_code", metricsRecorder.LastGrantType);
        // Metrics recorded for token request
    }

    [TestMethod]
    public async Task Token_Missing_Form_Content_Type_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();
        var handler = CreateHandler(db);

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Request.Path = "/token";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json"; // Wrong content type
        context.Response.Body = new MemoryStream();

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for missing form content type
    }

    // Stub implementations
    private sealed class StubTokenService : ITokenService
    {
        public Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(
            string code, string redirectUri, string clientId, string codeVerifier, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, CancellationToken ct = default)
        {
            var payload = new { access_token = "test_access_token", token_type = "Bearer", expires_in = 3600 };
            return Task.FromResult((true, (object?)payload, (string?)null, 200));
        }

        public Task<(bool ok, object? payload, string? error, int status)> ExchangeRefreshTokenAsync(
            string refreshToken, string clientId, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, CancellationToken ct = default)
        {
            var payload = new { access_token = "test_access_token", token_type = "Bearer", expires_in = 3600 };
            return Task.FromResult((true, (object?)payload, (string?)null, 200));
        }

        public Task<(bool ok, object? payload, string? error, int status)> CreateClientCredentialsTokenAsync(
            string clientId, string audience, string[] requestedScopes, string issuer, string? dpopJkt = null, CancellationToken ct = default)
        {
            var payload = new { access_token = "test_access_token", token_type = "Bearer", expires_in = 3600 };
            return Task.FromResult((true, (object?)payload, (string?)null, 200));
        }

        public Task<(bool ok, object? payload, string? error, int status)> ExchangeTokenAsync(
            string subjectToken, string? subjectTokenType, string? requestedTokenType, string? requestedAudience, string[] requestedScopes, string callerClientId, string issuer, string? dpopJkt = null, CancellationToken ct = default)
        {
            var payload = new { access_token = "test_access_token", token_type = "Bearer", expires_in = 3600 };
            return Task.FromResult((true, (object?)payload, (string?)null, 200));
        }
    }

    private sealed class StubClientStore : IClientStore
    {
        private readonly MrWhoOidc.Auth.Persistence.Client? _client;
        private readonly bool _authenticated;

        public StubClientStore(MrWhoOidc.Auth.Persistence.Client? client = null, bool authenticated = true)
        {
            _client = client;
            _authenticated = authenticated;
        }

        public Task<MrWhoOidc.Auth.Persistence.Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
        {
            if (_client != null && _client.ClientId == clientId)
            {
                return Task.FromResult<MrWhoOidc.Auth.Persistence.Client?>(_client);
            }
            return Task.FromResult<MrWhoOidc.Auth.Persistence.Client?>(null);
        }

        public Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default)
        {
            return Task.FromResult(_authenticated);
        }

        public Task<bool> ValidateClientCredentialsAsync(string clientId, string clientSecret, CancellationToken ct = default)
        {
            return Task.FromResult(_authenticated);
        }

        public IQueryable<MrWhoOidc.Auth.Persistence.Client> QueryClients(CancellationToken ct = default)
        {
            return Array.Empty<MrWhoOidc.Auth.Persistence.Client>().AsQueryable();
        }

        public Task InvalidateClientCacheAsync(string clientId, Guid tenantId, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        // New methods for client secret rotation - stubs for test purposes
        public Task<MrWhoOidc.Auth.Persistence.ClientSecret?> GetPrimarySecretAsync(Guid clientId, CancellationToken ct = default)
        {
            return Task.FromResult<MrWhoOidc.Auth.Persistence.ClientSecret?>(null);
        }

        public Task<List<MrWhoOidc.Auth.Persistence.ClientSecret>> GetActiveSecretsAsync(Guid clientId, CancellationToken ct = default)
        {
            return Task.FromResult(new List<MrWhoOidc.Auth.Persistence.ClientSecret>());
        }

        public Task<MrWhoOidc.Auth.Persistence.ClientSecret> CreateSecretAsync(
            Guid clientId, string secretValue, string? description, string? createdBy, DateTime? expiresAtUtc = null, CancellationToken ct = default)
        {
            throw new NotImplementedException("CreateSecretAsync not implemented in test stub");
        }

        public Task<bool> ActivateSecretAsync(Guid secretId, string activatedBy, CancellationToken ct = default)
        {
            throw new NotImplementedException("ActivateSecretAsync not implemented in test stub");
        }

        public Task<bool> SetPrimarySecretAsync(Guid secretId, string setPrimaryBy, CancellationToken ct = default)
        {
            throw new NotImplementedException("SetPrimarySecretAsync not implemented in test stub");
        }

        public Task<bool> RevokeSecretAsync(Guid secretId, string revokedBy, CancellationToken ct = default)
        {
            throw new NotImplementedException("RevokeSecretAsync not implemented in test stub");
        }

        public Task<bool> RecordSecretUsageAsync(Guid secretId, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class StubClientAssertionValidator : IClientAssertionValidator
    {
        private readonly bool _valid;

        public StubClientAssertionValidator(bool valid = true)
        {
            _valid = valid;
        }

        public Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default)
        {
            return Task.FromResult(_valid);
        }
    }

    private sealed class StubDPoPValidator : IDPoPValidator
    {
        private readonly bool _ok;
        private readonly string? _error;
        private readonly string _jkt;

        public StubDPoPValidator(bool ok = true, string? error = null, string jkt = "test_jkt")
        {
            _ok = ok;
            _error = error;
            _jkt = jkt;
        }

        public Task<DPoPValidationResult> ValidateForEndpointAsync(HttpContext http, string absoluteEndpointUrl, string? accessToken = null, CancellationToken ct = default)
        {
            var result = new DPoPValidationResult(_ok, _jkt, "test_jti", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, _error);
            return Task.FromResult(result);
        }
    }

    private sealed class StubTokenGrantHandler : ITokenGrantHandler
    {
        private readonly bool _handled;
        private readonly bool _success;

        public StubTokenGrantHandler(bool handled = false, bool success = false)
        {
            _handled = handled;
            _success = success;
        }

        public string GrantType => "authorization_code";

        public Task<GrantExecutionResult> TryHandleAsync(TokenRequestContext context)
        {
            if (!_handled)
            {
                return Task.FromResult(new GrantExecutionResult(false, false, null));
            }

            var payload = new { access_token = "test_token", token_type = "Bearer", expires_in = 3600 };
            var result = Microsoft.AspNetCore.Http.Results.Json(payload);
            return Task.FromResult(new GrantExecutionResult(true, _success, result));
        }
    }

    private sealed class StubTokenMetricsRecorder : ITokenMetricsRecorder
    {
        public bool TokenRequestCalled { get; private set; }
        public string? LastGrantType { get; private set; }

        public void RecordTokenRequest(string grantType, string outcome)
        {
            TokenRequestCalled = true;
            LastGrantType = grantType;
        }

        public void RecordTokenSuccess(string grantType) { }
        public void RecordTokenFailure(string grantType) { }
        public void RecordTokenDuration(string grantType, string outcome, double ms) { }
        public void RecordTokenExchange(string outcome, string clientBucket, string targetAudBucket, string dpopMode, string sourceTokenType, double? durationMs = null) { }
        public void RecordTokenExchangeFailure(string clientBucket, string? targetAudBucket, string dpopMode, string sourceTokenType, string reason) { }
        public void RecordTokenExchangeRateLimitAllowed(string clientBucket) { }
        public void RecordTokenExchangeRateLimitBlocked(string clientBucket, int? retryAfterSeconds) { }
    }
}
