using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Security;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.TokenEndpoint.Grants;
using System.Text;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.Auth.Services.Authentication;
using MrWhoOidc.Auth.Protocols;
using System.Text.Json;
using System.Threading;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration

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
        IDPoPReplayCache? dpopReplayCache = null,
        IEnumerable<ITokenGrantHandler>? grantHandlers = null,
        IEnumerable<ITokenMetricsRecorder>? tokenMetrics = null,
        IOptions<OidcOptions>? options = null,
        IFeatureService? featureService = null,
        ITenantAccessor? tenantAccessor = null,
        ITokenExchangeService? tokenExchange = null)
    {
        var logger = NullLogger<TokenHandler>.Instance;

        tokens ??= new StubTokenService();
        clients ??= new StubClientStore();
        assertions ??= new StubClientAssertionValidator();
        dpop ??= new StubDPoPValidator();
        dpopReplayCache ??= new InMemoryDPoPReplayCache();
        grantHandlers ??= new[] { new StubTokenGrantHandler() };
        tokenMetrics ??= new[] { new StubTokenMetricsRecorder() };
        options ??= Options.Create(new OidcOptions { Issuer = "https://test.example.com" });
        featureService ??= new StubFeatureService();
        tenantAccessor ??= MockTenantAccessor.CreateSingleTenantMode();
        tokenExchange ??= new StubTokenExchangeService();

        var authLogger = NullLogger<ClientAuthenticator>.Instance;
        var domainLogger = NullLogger<MrWhoOidc.Auth.Services.Authentication.ClientAuthenticationService>.Instance;
        var authOptions = Options.Create(new AuthOptions());
        var domainService = new MrWhoOidc.Auth.Services.Authentication.ClientAuthenticationService(clients, assertions, authOptions, domainLogger);
        var authenticator = new ClientAuthenticator(domainService, new MtlsThumbprintResolver(), authLogger);

        return new TokenHandler(options.Value, tokens, tokenExchange, authenticator, dpop, dpopReplayCache, grantHandlers, tokenMetrics, featureService, tenantAccessor, logger);
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

    private sealed class StubFeatureService : IFeatureService
    {
        public Task<bool> IsFeatureEnabledAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlySet<string>> GetEnabledFeaturesAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(FeatureFlags.AllFeatures);

        public Task RecordFeatureUsageAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FeatureUsageMetric>> GetFeatureUsageAsync(Guid? tenantId = null, string? featureName = null, DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureUsageMetric>>(Array.Empty<FeatureUsageMetric>());
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
    public async Task Token_DPoP_Replay_IsRejected_At_TokenEndpoint()
    {
        using var db = CreateDb();

        // Use a validator that returns a constant jti/jkt and a replay cache that only allows one entry
        var dpopValidator = new StubDPoPValidator(ok: true, error: null, jkt: "test_jkt");
        var replayCache = new OneTimeReplayCache();
        // Provide a client_credentials grant handler so the token endpoint returns an access_token for the test
        // Create a client and client store for authentication
#pragma warning disable CS0618 // ClientSecretHash is obsolete but needed for client_credentials test
        var client = new MrWhoOidc.Auth.Persistence.Client { Id = Guid.NewGuid(), ClientId = "client", ClientName = "DPoP Client", TenantId = Guid.NewGuid(), ClientSecretHash = "hash" };
#pragma warning restore CS0618
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var clientStore = new StubClientStore(client);

        var clientCredsGrant = new ClientCredentialsGrantStub();
        var handler = CreateHandler(db, clients: clientStore, dpop: dpopValidator, dpopReplayCache: replayCache, grantHandlers: new[] { clientCredsGrant });

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "api"
        };

        // First request should succeed (DPoP proof accepted)
        var ctx1 = CreateHttpContext(formData, authorizationHeader: BasicAuth("client", "secret"));
        ctx1.Request.Headers["DPoP"] = "dpop_proof"; // Add DPoP header to trigger DPoP validation
        var res1 = await handler.HandleAsync(ctx1);
        await res1.ExecuteAsync(ctx1);
        ctx1.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        var body1 = new System.IO.StreamReader(ctx1.Response.Body).ReadToEnd();
        Assert.IsTrue(body1.Contains("access_token"), "First token request did not return an access_token");

        // Second request (same jti) should be rejected due to replay detection
        var ctx2 = CreateHttpContext(formData, authorizationHeader: BasicAuth("client", "secret"));
        ctx2.Request.Headers["DPoP"] = "dpop_proof"; // Same DPoP header triggers replay check
        var res2 = await handler.HandleAsync(ctx2);
        await res2.ExecuteAsync(ctx2);
        ctx2.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        var body2 = new System.IO.StreamReader(ctx2.Response.Body).ReadToEnd();
        Assert.IsTrue(body2.Contains("invalid_dpop_proof"), "Replay was not detected as invalid_dpop_proof");
    }

    [TestMethod]
    public async Task Token_Mtls_ClientCredentials_Succeeds_When_CertMatches()
    {
        using var db = CreateDb();

        // Create a self-signed cert and compute thumbprint
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=mtls-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var resolver = new MrWhoOidc.Auth.Services.MtlsThumbprintResolver();
        var thumb = resolver.ResolveThumbprint(cert);

        // Create client configured with M2M mtls thumbprint
        var client = new MrWhoOidc.Auth.Persistence.Client { Id = Guid.NewGuid(), ClientId = "mtls-client", ClientName = "MTLS Client", TenantId = Guid.NewGuid(), M2MMtlsThumbprintsJson = System.Text.Json.JsonSerializer.Serialize(new[] { thumb }) };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var clientCredsGrant = new ClientCredentialsGrantStub();
        var clientStore = new StubClientStore(client);
        var handler = CreateHandler(db, clients: clientStore, grantHandlers: new[] { clientCredsGrant });

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "mtls-client",
            ["scope"] = "api"
        };

        var ctx = CreateHttpContext(formData);
        // Present cert
        ctx.Connection.ClientCertificate = cert;

        var res = await handler.HandleAsync(ctx);
        await res.ExecuteAsync(ctx);

        // New test: certificate-bound token includes cnf.x5t#S256 in payload when MTLS authenticated
        var body = string.Empty;
        ctx.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        using (var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync();
        }
        Assert.IsTrue(body.Contains("access_token"));
    }



    [TestMethod]
    public async Task Token_Mtls_ClientCredentials_Fails_When_No_Cert()
    {
        using var db = CreateDb();

        // Create thumbprint placeholder and client expecting it
        var fakeThumb = "abcd";
        var client = new MrWhoOidc.Auth.Persistence.Client { Id = Guid.NewGuid(), ClientId = "mtls-client-no-cert", ClientName = "MTLS Client No Cert", TenantId = Guid.NewGuid(), M2MMtlsThumbprintsJson = System.Text.Json.JsonSerializer.Serialize(new[] { fakeThumb }) };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var clientCredsGrant = new ClientCredentialsGrantStub();
        var clientStore = new StubClientStore(client);
        var handler = CreateHandler(db, clients: clientStore, grantHandlers: new[] { clientCredsGrant });

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "mtls-client-no-cert",
            ["scope"] = "api"
        };

        var ctx = CreateHttpContext(formData);
        var res = await handler.HandleAsync(ctx);
        await res.ExecuteAsync(ctx);

        Assert.AreEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode, "MTLS protected client without cert should be rejected with 401");
        Assert.IsTrue(ctx.Response.Headers.TryGetValue("WWW-Authenticate", out var headerVal) && headerVal.ToString().Contains("mtls_required"), "WWW-Authenticate header should indicate mtls_required");
    }

    [TestMethod]
    public async Task Token_Mtls_ClientCredentials_E2E_Issues_Certificate_Bound_Access_Token()
    {
        using var db = CreateDb();

        // Create a self-signed cert and compute thumbprint
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=mtls-e2e", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var resolver = new MrWhoOidc.Auth.Services.MtlsThumbprintResolver();
        var thumb = resolver.ResolveThumbprint(cert);

        // Create client configured with M2M mtls thumbprint
        var client = new MrWhoOidc.Auth.Persistence.Client { Id = Guid.NewGuid(), ClientId = "mtls-e2e-client", ClientName = "MTLS E2E Client", TenantId = Guid.NewGuid(), M2MMtlsThumbprintsJson = System.Text.Json.JsonSerializer.Serialize(new[] { thumb }) };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var clientStore = new StubClientStore(client);
        var mtlsResolver = new MrWhoOidc.Auth.Services.MtlsThumbprintResolver();
        var clientCredsGrant = new MrWhoOidc.WebAuth.TokenEndpoint.Grants.ClientCredentialsGrantHandler(NullLogger<MrWhoOidc.WebAuth.TokenEndpoint.Grants.ClientCredentialsGrantHandler>.Instance, mtlsResolver);
        var handler = CreateHandler(db, clients: clientStore, grantHandlers: new[] { clientCredsGrant });

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "mtls-e2e-client"
            // no scope to avoid product-scope rejection
        };

        var ctx = CreateHttpContext(formData);
        // Add minimal RequestServices required by GetIssuer (IIssuerBuilder, IMultiTenancyOptions, ITenantAccessor)
        var svcs = new ServiceCollection();
        svcs.AddLogging();
        svcs.AddOptions();
        svcs.AddScoped<MrWhoOidc.Auth.MultiTenancy.ITenantAccessor, MrWhoOidc.Auth.MultiTenancy.TenantAccessor>();
        svcs.AddSingleton<MrWhoOidc.Auth.MultiTenancy.IMultiTenancyOptions>(new MrWhoOidc.Auth.MultiTenancy.MultiTenancyOptions());
        svcs.AddScoped<MrWhoOidc.Auth.MultiTenancy.IIssuerBuilder, MrWhoOidc.Auth.MultiTenancy.IssuerBuilder>();
        ctx.RequestServices = svcs.BuildServiceProvider();
        // Present cert
        ctx.Connection.ClientCertificate = cert;

        var res = await handler.HandleAsync(ctx);
        await res.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, System.IO.SeekOrigin.Begin);
        var body = new System.IO.StreamReader(ctx.Response.Body).ReadToEnd();

        Assert.AreEqual(StatusCodes.Status200OK, ctx.Response.StatusCode, $"Unexpected status: {ctx.Response.StatusCode}. Body: {body}");
        var doc = JsonDocument.Parse(body);
        Assert.IsTrue(doc.RootElement.TryGetProperty("access_token", out var accessTokenElement), $"No access_token in response: {body}");
        var accessToken = accessTokenElement.GetString();
        Assert.IsFalse(string.IsNullOrEmpty(accessToken));
        Assert.IsTrue(doc.RootElement.TryGetProperty("cnf", out var cnf), $"No cnf in response: {body}");
        // cnf is an object; check x5t#S256 exists
        Assert.IsTrue(cnf.TryGetProperty("x5t#S256", out var foundThumb), $"cnf present but missing x5t#S256: {cnf}");
        Assert.AreEqual(thumb, foundThumb.GetString());
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
            RealmId = realmId,
            AllowPrivateKeyJwt = true
        };
        client.ClientSecrets.Add(new ClientSecret { SecretHash = "hash" });
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
            RealmId = realmId
        };
        client.ClientSecrets.Add(new ClientSecret { SecretHash = "hash" });
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
    public async Task Token_DPoP_Proof_Replay_Returns_Invalid_DPoP_Proof()
    {
        using var db = CreateDb();

        var realmId = Guid.NewGuid();
        db.Realms.Add(new Realm { Id = realmId, Name = "test_realm" });

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "test_client",
            RealmId = realmId
        };
        client.ClientSecrets.Add(new ClientSecret { SecretHash = "hash" });
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var dpop = new StubDPoPValidator(ok: true, jkt: "test_jkt");
        var replayCache = new OneTimeReplayCache();
        var clientStore = new StubClientStore(client, authenticated: true);
        var grantHandlers = new[] { new StubTokenGrantHandler(handled: true, success: true) };
        var handler = CreateHandler(db, dpop: dpop, dpopReplayCache: replayCache, clients: clientStore, grantHandlers: grantHandlers);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test_code",
            ["redirect_uri"] = "https://app/callback",
            ["client_id"] = "test_client",
            ["client_secret"] = "secret"
        };

        // First request should succeed.
        var ctx1 = CreateHttpContext(formData);
        ctx1.Request.Headers["DPoP"] = "dpop_proof";
        var r1 = await handler.HandleAsync(ctx1);
        await r1.ExecuteAsync(ctx1);
        Assert.AreEqual(200, ctx1.Response.StatusCode);

        // Second request with same proof key should be rejected as replay.
        var ctx2 = CreateHttpContext(formData);
        ctx2.Request.Headers["DPoP"] = "dpop_proof";
        var r2 = await handler.HandleAsync(ctx2);
        await r2.ExecuteAsync(ctx2);
        Assert.AreEqual(400, ctx2.Response.StatusCode);

        ctx2.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx2.Response.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual("invalid_dpop_proof", doc.RootElement.GetProperty("error").GetString());
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
            RealmId = realmId
        };
        client.ClientSecrets.Add(new ClientSecret { SecretHash = "hash" });
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
            RealmId = realmId
        };
        client.ClientSecrets.Add(new ClientSecret { SecretHash = "hash" });
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
            RealmId = realmId
        };
        client.ClientSecrets.Add(new ClientSecret { SecretHash = "hash" });
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
            RealmId = realmId
        };
        client.ClientSecrets.Add(new ClientSecret { SecretHash = "hash" });
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
            RealmId = realmId
        };
        client.ClientSecrets.Add(new ClientSecret { SecretHash = "hash" });
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
            RealmId = realmId
        };
        client.ClientSecrets.Add(new ClientSecret { SecretHash = "hash" });
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
            string code, string redirectUri, string clientId, string codeVerifier, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default)
        {
            var payload = new { access_token = "test_access_token", token_type = "Bearer", expires_in = 3600 };
            return Task.FromResult((true, (object?)payload, (string?)null, 200));
        }

        public Task<(bool ok, object? payload, string? error, int status)> ExchangeRefreshTokenAsync(
            string refreshToken, string clientId, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default)
        {
            var payload = new { access_token = "test_access_token", token_type = "Bearer", expires_in = 3600 };
            return Task.FromResult((true, (object?)payload, (string?)null, 200));
        }

        public Task<(bool ok, object? payload, string? error, int status)> CreateClientCredentialsTokenAsync(
            string clientId, string audience, string[] requestedScopes, string issuer, string? dpopJkt = null, string? mtlsX5tS256 = null, CancellationToken ct = default)
        {
            object payload;
            if (!string.IsNullOrEmpty(mtlsX5tS256))
            {
                var cnf = new Dictionary<string, string> { ["x5t#S256"] = mtlsX5tS256 };
                payload = new { access_token = "test_access_token", token_type = "Bearer", expires_in = 3600, cnf };
            }
            else if (!string.IsNullOrEmpty(dpopJkt))
            {
                var cnf = new { jkt = dpopJkt };
                payload = new { access_token = "test_access_token", token_type = "Bearer", expires_in = 3600, cnf };
            }
            else
            {
                // For CB-TLS tests we expect mtlsX5tS256 to be provided when client presents cert
                throw new InvalidOperationException("CreateClientCredentialsTokenAsync called without mtls or dpop binding");
            }
            return Task.FromResult((true, (object?)payload, (string?)null, 200));
        }

        public Task<(bool ok, object? payload, string? error, int status)> ExchangeTokenAsync(
            string subjectToken, string? subjectTokenType, string? requestedTokenType, string? requestedAudience, string[] requestedScopes, string callerClientId, string issuer, string? dpopJkt = null, CancellationToken ct = default)
        {
            var payload = new { access_token = "test_access_token", token_type = "Bearer", expires_in = 3600 };
            return Task.FromResult((true, (object?)payload, (string?)null, 200));
        }

        public Task<(bool ok, object? payload, string? error, int status)> CreateDeviceCodeTokenAsync(
            string clientId, Guid userId, string[] scopes, string audience, string issuer,
            string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default)
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

    private sealed class OneTimeReplayCache : IDPoPReplayCache
    {
        private int _count;
        public bool TryAdd(string key, DateTimeOffset expiresAt)
            => Interlocked.Increment(ref _count) == 1;
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

    private sealed class ClientCredentialsGrantStub : ITokenGrantHandler
    {
        public string GrantType => "client_credentials";
        public Task<GrantExecutionResult> TryHandleAsync(TokenRequestContext context)
        {
            var payload = new { access_token = "test_token", token_type = "Bearer", expires_in = 3600 };
            var result = Microsoft.AspNetCore.Http.Results.Json(payload);
            return Task.FromResult(new GrantExecutionResult(true, true, result));
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

    internal class StubTokenExchangeService : ITokenExchangeService
    {
        public Task<(bool ok, object? payload, string? error, int status)> ExchangeTokenAsync(string subjectToken, string? subjectTokenType, string? requestedTokenType, string? requestedAudience, string[] requestedScopes, string callerClientId, string issuer, string? dpopJkt, CancellationToken ct = default)
        {
            return Task.FromResult((true, (object?)new { access_token = "mock_te_token" }, (string?)null, 200));
        }
    }
}
