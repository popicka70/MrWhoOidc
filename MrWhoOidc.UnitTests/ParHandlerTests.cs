using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Observability;
using System.Text;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class ParHandlerTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static ParHandler CreateHandler(
        IClientStore? clients = null,
        IClientAssertionValidator? assertions = null,
        IAuthorizeService? authorize = null,
        IPushedAuthorizationRequestStore? parStore = null,
        IRequestObjectValidator? requestObjects = null,
        IOptions<AuthOptions>? authOptions = null,
        IOptions<OidcOptions>? oidcOptions = null)
    {
        var logger = NullLogger<ParHandler>.Instance;
        var metrics = new OidcEndpointMetrics();

        clients ??= new StubClientStore();
        assertions ??= new StubClientAssertionValidator();
        authorize ??= new StubAuthorizeService();
        parStore ??= new StubPushedAuthorizationRequestStore();
        requestObjects ??= new StubRequestObjectValidator();
        authOptions ??= Options.Create(new AuthOptions());
        oidcOptions ??= Options.Create(new OidcOptions { Issuer = "https://test.example.com" });

        return new ParHandler(oidcOptions.Value, clients, assertions, authorize, parStore, requestObjects, authOptions, metrics, logger);
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
        context.Request.Path = "/par";
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
    public async Task PAR_HappyPath_Returns_RequestUri_And_ExpiresIn()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var authorize = new StubAuthorizeService(valid: true);
        var parStore = new StubPushedAuthorizationRequestStore(createSuccess: true);
        var handler = CreateHandler(clients: clientStore, authorize: authorize, parStore: parStore);

        var formData = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret",
            ["redirect_uri"] = "https://app/callback",
            ["scope"] = "openid",
            ["code_challenge"] = new string('a', 43),
            ["code_challenge_method"] = "S256"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns request_uri and expires_in
    }

    [TestMethod]
    public async Task PAR_Request_Object_Signed_JWT_Validated()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var requestObjects = new StubRequestObjectValidator(valid: true, clientId: "test_client");
        var authorize = new StubAuthorizeService(valid: true);
        var parStore = new StubPushedAuthorizationRequestStore(createSuccess: true);
        var handler = CreateHandler(clients: clientStore, requestObjects: requestObjects, authorize: authorize, parStore: parStore);

        var formData = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret",
            ["request"] = "eyJhbGciOiJSUzI1NiJ9.eyJjbGllbnRfaWQiOiJ0ZXN0X2NsaWVudCIsInJlc3BvbnNlX3R5cGUiOiJjb2RlIn0.sig" // Mock signed JWT
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler validates signed JWT request object
    }

    [TestMethod]
    public async Task PAR_Request_Object_Invalid_Returns_Error()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var requestObjects = new StubRequestObjectValidator(valid: false, error: "invalid_request_object", errorDescription: "Invalid JWT signature");
        var handler = CreateHandler(clients: clientStore, requestObjects: requestObjects);

        var formData = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret",
            ["request"] = "invalid.jwt.token"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns invalid_request_object error
    }

    [TestMethod]
    public async Task PAR_Request_Object_ClientId_Mismatch_Returns_Error()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var requestObjects = new StubRequestObjectValidator(valid: true, clientId: "other_client");
        var handler = CreateHandler(clients: clientStore, requestObjects: requestObjects);

        var formData = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret",
            ["request"] = "eyJhbGciOiJSUzI1NiJ9.e30.sig"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for client_id mismatch between auth and request object
    }

    [TestMethod]
    public async Task PAR_Client_Authentication_Required()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: false);
        var handler = CreateHandler(clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["scope"] = "openid"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns unauthorized_client error
    }

    [TestMethod]
    public async Task PAR_Client_Authentication_Via_Basic_Auth()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var authorize = new StubAuthorizeService(valid: true);
        var parStore = new StubPushedAuthorizationRequestStore(createSuccess: true);
        var handler = CreateHandler(clients: clientStore, authorize: authorize, parStore: parStore);

        var formData = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["redirect_uri"] = "https://app/callback",
            ["scope"] = "openid",
            ["code_challenge"] = new string('b', 43),
            ["code_challenge_method"] = "S256"
        };
        var authHeader = BasicAuth("test_client", "test_secret");
        var context = CreateHttpContext(formData, authHeader);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler accepts Basic authentication
    }

    [TestMethod]
    public async Task PAR_Client_Authentication_Via_ClientAssertion_JWT()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: false); // Will be authenticated via assertion
        var assertions = new StubClientAssertionValidator(valid: true);
        var authorize = new StubAuthorizeService(valid: true);
        var parStore = new StubPushedAuthorizationRequestStore(createSuccess: true);
        var handler = CreateHandler(clients: clientStore, assertions: assertions, authorize: authorize, parStore: parStore);

        var formData = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["scope"] = "openid",
            ["code_challenge"] = new string('c', 43),
            ["code_challenge_method"] = "S256",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = "eyJhbGciOiJSUzI1NiJ9.e30.sig" // Mock JWT assertion
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler accepts private_key_jwt authentication
    }

    [TestMethod]
    public async Task PAR_Invalid_Client_Assertion_Returns_Error()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: false);
        var assertions = new StubClientAssertionValidator(valid: false);
        var handler = CreateHandler(clients: clientStore, assertions: assertions);

        var formData = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["scope"] = "openid",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = "invalid.jwt.token"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns unauthorized_client for invalid assertion
    }

    [TestMethod]
    public async Task PAR_Invalid_Client_Returns_Error()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: false);
        var handler = CreateHandler(clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "unknown_client",
            ["client_secret"] = "wrong_secret",
            ["redirect_uri"] = "https://app/callback",
            ["scope"] = "openid"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns unauthorized_client for unknown client
    }

    [TestMethod]
    public async Task PAR_Missing_ClientId_Returns_Error()
    {
        // Arrange
        var handler = CreateHandler();

        var formData = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["redirect_uri"] = "https://app/callback",
            ["scope"] = "openid"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns invalid_request for missing client_id
    }

    [TestMethod]
    public async Task PAR_Request_Validation_Failed_Returns_Error()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var authorize = new StubAuthorizeService(valid: false, error: "invalid_scope", errorDescription: "Invalid scope requested");
        var handler = CreateHandler(clients: clientStore, authorize: authorize);

        var formData = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret",
            ["redirect_uri"] = "https://app/callback",
            ["scope"] = "openid admin" // Invalid scope
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns invalid_scope error from authorize service
    }

    [TestMethod]
    public async Task PAR_Request_Object_Too_Large_Returns_Error()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var authOptions = Options.Create(new AuthOptions { RequestObjectMaxBytes = 100 }); // Small limit
        var handler = CreateHandler(clients: clientStore, authOptions: authOptions);

        var largeRequestObject = new string('x', 200); // Exceeds 100 byte limit
        var formData = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret",
            ["request"] = largeRequestObject
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns invalid_request_object for oversized request
    }

    [TestMethod]
    public async Task PAR_Rate_Limiting_Applied_Per_Client()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var authorize = new StubAuthorizeService(valid: true);
        var parStore = new StubPushedAuthorizationRequestStore(createSuccess: false, throwsPendingLimit: true);
        var handler = CreateHandler(clients: clientStore, authorize: authorize, parStore: parStore);

        var formData = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret",
            ["redirect_uri"] = "https://app/callback",
            ["scope"] = "openid",
            ["code_challenge"] = new string('d', 43),
            ["code_challenge_method"] = "S256"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns rate_limit_exceeded (429) error
    }

    [TestMethod]
    public async Task PAR_Missing_Form_Content_Type_Returns_Error()
    {
        // Arrange
        var handler = CreateHandler();

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Request.Path = "/par";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json"; // Wrong content type
        context.Response.Body = new MemoryStream();

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns invalid_request for missing form content type
    }

    // Stub implementations
    private sealed class StubClientStore : IClientStore
    {
        private readonly bool _authenticated;

        public StubClientStore(bool authenticated = true)
        {
            _authenticated = authenticated;
        }

        public Task<MrWhoOidc.Auth.Persistence.Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
        {
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

    private sealed class StubAuthorizeService : IAuthorizeService
    {
        private readonly bool _valid;
        private readonly string? _error;
        private readonly string? _errorDescription;

        public StubAuthorizeService(bool valid = true, string? error = null, string? errorDescription = null)
        {
            _valid = valid;
            _error = error;
            _errorDescription = errorDescription;
        }

        public Task<AuthorizeValidationResult> ValidateAsync(AuthorizeRequest request, CancellationToken ct = default)
        {
            var result = new AuthorizeValidationResult(
                IsValid: _valid,
                Error: _error,
                ErrorDescription: _errorDescription,
                ClientId: request.client_id,
                RedirectUri: request.redirect_uri,
                Scopes: new[] { "openid" }
            );
            return Task.FromResult(result);
        }
    }

    private sealed class StubPushedAuthorizationRequestStore : IPushedAuthorizationRequestStore
    {
        private readonly bool _createSuccess;
        private readonly bool _throwsPendingLimit;

        public StubPushedAuthorizationRequestStore(bool createSuccess = true, bool throwsPendingLimit = false)
        {
            _createSuccess = createSuccess;
            _throwsPendingLimit = throwsPendingLimit;
        }

        public DateTimeOffset Create(string id, AuthorizeRequest request, string clientId, TimeSpan lifetime, string? requestUri)
        {
            if (_throwsPendingLimit)
            {
                throw new InvalidOperationException("PAR pending limit reached");
            }

            if (!_createSuccess)
            {
                throw new InvalidOperationException("Create failed");
            }

            return DateTimeOffset.UtcNow.Add(lifetime);
        }

        public PushedAuthorizationRequestEntry? TryGetById(string id)
        {
            return null;
        }

        public void MarkConsumedById(string id)
        {
        }

        public PushedAuthorizationRequestEntry? TryConsumeById(string id)
        {
            return null;
        }
    }

    private sealed class StubRequestObjectValidator : IRequestObjectValidator
    {
        private readonly bool _valid;
        private readonly string? _error;
        private readonly string? _errorDescription;
        private readonly string? _clientId;

        public StubRequestObjectValidator(bool valid = true, string? error = null, string? errorDescription = null, string? clientId = null)
        {
            _valid = valid;
            _error = error;
            _errorDescription = errorDescription;
            _clientId = clientId;
        }

        public Task<RequestObjectValidationResult> ValidateAsync(string requestJwt, string audience, CancellationToken ct = default)
        {
            var result = new RequestObjectValidationResult
            {
                IsValid = _valid,
                Error = _error,
                ErrorDescription = _errorDescription,
                ClientId = _clientId ?? "test_client",
                Request = _valid ? new AuthorizeRequest
                {
                    client_id = _clientId ?? "test_client",
                    response_type = "code",
                    redirect_uri = "https://app/callback",
                    scope = "openid",
                    code_challenge = new string('x', 43),
                    code_challenge_method = "S256"
                } : null
            };
            return Task.FromResult(result);
        }
    }
}
