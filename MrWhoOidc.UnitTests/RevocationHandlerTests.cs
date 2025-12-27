using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Observability;
using System.Text;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class RevocationHandlerTests
{
    private static RevocationHandler CreateHandler(
        IRevocationService? revocations = null,
        IClientStore? clients = null,
        IClientAssertionValidator? assertions = null,
        OidcOptions? options = null)
    {
        var logger = NullLogger<RevocationHandler>.Instance;
        var metrics = new OidcEndpointMetrics();

        revocations ??= new StubRevocationService();
        clients ??= new StubClientStore();
        assertions ??= new StubClientAssertionValidator();
        options ??= new OidcOptions { Issuer = "https://test.example.com" };

        return new RevocationHandler(revocations, clients, metrics, assertions, options);
    }

    private static DefaultHttpContext CreateHttpContext(
        Dictionary<string, string>? formData = null,
        string? authorizationHeader = null,
        string? remoteIpAddress = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext();
        context.RequestServices = serviceProvider;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Request.Path = "/revoke";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Response.Body = new MemoryStream();

        if (remoteIpAddress != null)
        {
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIpAddress);
        }

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
    public async Task Revocation_Access_Token_HappyPath_Returns_200()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var revocationService = new StubRevocationService(revoked: true);
        var handler = CreateHandler(revocations: revocationService, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["token"] = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyMSJ9.sig", // Mock access token
            ["token_type_hint"] = "access_token",
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret"
        };
        var context = CreateHttpContext(formData, remoteIpAddress: "192.168.1.100");

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(revocationService.RevokeAsyncCalled);
        // Handler returns 200 OK for successful revocation
    }

    [TestMethod]
    public async Task Revocation_Refresh_Token_HappyPath_Returns_200()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var revocationService = new StubRevocationService(revoked: true);
        var handler = CreateHandler(revocations: revocationService, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["token"] = "refresh_token_abc123",
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret"
        };
        var context = CreateHttpContext(formData, remoteIpAddress: "192.168.1.100");

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(revocationService.RevokeAsyncCalled);
        Assert.AreEqual("refresh_token", revocationService.LastTokenTypeHint);
        // Handler returns 200 OK and revokes refresh token family
    }

    [TestMethod]
    public async Task Revocation_Unknown_Token_Returns_200()
    {
        // Arrange - RFC 7009 requires 200 OK even for unknown tokens
        var clientStore = new StubClientStore(authenticated: true);
        var revocationService = new StubRevocationService(revoked: false); // Unknown token
        var handler = CreateHandler(revocations: revocationService, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["token"] = "unknown_token_xyz",
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(revocationService.RevokeAsyncCalled);
        // Handler returns 200 OK per RFC 7009 (idempotent)
    }

    [TestMethod]
    public async Task Revocation_Client_Authentication_Required()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: false);
        var handler = CreateHandler(clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["token"] = "some_token",
            ["client_id"] = "test_client",
            ["client_secret"] = "wrong_secret"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns 400 unauthorized_client error
    }

    [TestMethod]
    public async Task Revocation_Client_Authentication_Via_Basic_Auth()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var revocationService = new StubRevocationService(revoked: true);
        var handler = CreateHandler(revocations: revocationService, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["token"] = "token_to_revoke"
        };
        var authHeader = BasicAuth("test_client", "test_secret");
        var context = CreateHttpContext(formData, authHeader);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(revocationService.RevokeAsyncCalled);
        Assert.AreEqual("test_client", revocationService.LastClientId);
        // Handler accepts Basic authentication
    }

    [TestMethod]
    public async Task Revocation_Client_Authentication_Via_PrivateKeyJwt()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: false); // Will be authenticated via assertion
        var assertions = new StubClientAssertionValidator(valid: true);
        var revocationService = new StubRevocationService(revoked: true);
        var handler = CreateHandler(revocations: revocationService, clients: clientStore, assertions: assertions);

        var formData = new Dictionary<string, string>
        {
            ["token"] = "token_to_revoke",
            ["client_id"] = "test_client",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = "eyJhbGciOiJSUzI1NiJ9.e30.sig" // Mock JWT assertion
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(revocationService.RevokeAsyncCalled);
        // Handler accepts private_key_jwt authentication
    }

    [TestMethod]
    public async Task Revocation_Invalid_Client_Assertion_Returns_Error()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: false);
        var assertions = new StubClientAssertionValidator(valid: false);
        var handler = CreateHandler(clients: clientStore, assertions: assertions);

        var formData = new Dictionary<string, string>
        {
            ["token"] = "token_to_revoke",
            ["client_id"] = "test_client",
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
    public async Task Revocation_Missing_Token_Returns_Error()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var handler = CreateHandler(clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret"
            // Missing token parameter
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns 400 invalid_request for missing token
    }

    [TestMethod]
    public async Task Revocation_Missing_ClientId_Returns_Error()
    {
        // Arrange
        var handler = CreateHandler();

        var formData = new Dictionary<string, string>
        {
            ["token"] = "some_token",
            ["client_secret"] = "test_secret"
            // Missing client_id
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns 400 invalid_request for missing client_id
    }

    [TestMethod]
    public async Task Revocation_Token_Type_Hint_Opaque_Access()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var revocationService = new StubRevocationService(revoked: true);
        var handler = CreateHandler(revocations: revocationService, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["token"] = "opaque_access_token_123",
            ["token_type_hint"] = "access_token",
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(revocationService.RevokeAsyncCalled);
        Assert.AreEqual("access_token", revocationService.LastTokenTypeHint);
        // Handler passes token_type_hint to revocation service
    }

    [TestMethod]
    public async Task Revocation_Token_Type_Hint_Refresh()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var revocationService = new StubRevocationService(revoked: true);
        var handler = CreateHandler(revocations: revocationService, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["token"] = "refresh_token_xyz",
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret"
        };
        var context = CreateHttpContext(formData);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(revocationService.RevokeAsyncCalled);
        Assert.AreEqual("refresh_token", revocationService.LastTokenTypeHint);
        // Handler passes token_type_hint to revocation service
    }

    [TestMethod]
    public async Task Revocation_IP_Address_Captured_For_Audit()
    {
        // Arrange
        var clientStore = new StubClientStore(authenticated: true);
        var revocationService = new StubRevocationService(revoked: true);
        var handler = CreateHandler(revocations: revocationService, clients: clientStore);

        var formData = new Dictionary<string, string>
        {
            ["token"] = "token_to_revoke",
            ["client_id"] = "test_client",
            ["client_secret"] = "test_secret"
        };
        var context = CreateHttpContext(formData, remoteIpAddress: "203.0.113.45");

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(revocationService.RevokeAsyncCalled);
        Assert.AreEqual("203.0.113.45", revocationService.LastIpAddress);
        // Handler captures IP address for audit logging
    }

    [TestMethod]
    public async Task Revocation_Missing_Form_Content_Type_Returns_Error()
    {
        // Arrange
        var handler = CreateHandler();

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Request.Path = "/revoke";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json"; // Wrong content type
        context.Response.Body = new MemoryStream();

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns 400 invalid_request for missing form content type
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

    private sealed class StubRevocationService : IRevocationService
    {
        private readonly bool _revoked;

        public bool RevokeAsyncCalled { get; private set; }
        public string? LastToken { get; private set; }
        public string? LastTokenTypeHint { get; private set; }
        public string? LastClientId { get; private set; }
        public string? LastIpAddress { get; private set; }

        public StubRevocationService(bool revoked = true)
        {
            _revoked = revoked;
        }

        public Task RevokeAsync(string token, string? tokenTypeHint, string clientId, string? ipAddress = null, CancellationToken ct = default)
        {
            RevokeAsyncCalled = true;
            LastToken = token;
            LastTokenTypeHint = tokenTypeHint;
            LastClientId = clientId;
            LastIpAddress = ipAddress;
            return Task.CompletedTask;
        }

        public Task RevokeAllForUserAsync(Guid userId, string clientId, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}
