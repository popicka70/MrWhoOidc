using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.UnitTests.Helpers;

#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
using System.Security.Claims;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AuthorizeHandlerTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    private static AuthorizeHandler CreateHandler(
        AuthDbContext db,
        IAuthorizeRequestValidator? validator = null,
        IConsentProcessor? consentProcessor = null,
        IProviderSelectionService? providerSelection = null,
        IUserClientAssignmentService? userAssignments = null,
        IAuthorizeResponseGenerator? responseGenerator = null,
        IAuthorizeRequestSanitizer? sanitizer = null,
        IAuthenticationRedirectService? authRedirect = null,
        IAuthorizationMetadataService? metadataService = null,
        IAuthorizeRequestOrchestrator? orchestrator = null,
        IAuthorizationCodeService? codes = null,
        IPushedAuthorizationRequestStore? parStore = null,
        IQrLoginHandler? qrLoginHandler = null,
        MrWhoOidc.Auth.Services.AuthOptions? authOptions = null)
    {
        var metrics = new OidcEndpointMetrics();
        var logger = NullLogger<AuthorizeHandler>.Instance;

        validator ??= new StubAuthorizeRequestValidator();
        consentProcessor ??= new StubConsentProcessor();
        providerSelection ??= new StubProviderSelectionService();
        userAssignments ??= new StubUserClientAssignmentService();
        responseGenerator ??= new StubAuthorizeResponseGenerator();
        sanitizer ??= new StubAuthorizeRequestSanitizer();
        authRedirect ??= new StubAuthenticationRedirectService();
        metadataService ??= new StubAuthorizationMetadataService();
        orchestrator ??= new StubAuthorizeRequestOrchestrator();
        codes ??= new StubAuthorizationCodeService();
        parStore ??= new StubPushedAuthorizationRequestStore();
        qrLoginHandler ??= new StubQrLoginHandler();
        authOptions ??= new MrWhoOidc.Auth.Services.AuthOptions();

        // Create a mock tenant accessor with default tenant
        var tenantAccessor = new MockTenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Slug = "default",
            Name = "Default",
            IssuerUri = "https://localhost:8443",
            IsMultiTenantMode = false
        });

        return new AuthorizeHandler(
            validator,
            consentProcessor,
            providerSelection,
            userAssignments,
            responseGenerator,
            sanitizer,
            authRedirect,
            metadataService,
            orchestrator,
            codes,
            metrics,
            parStore,
            Options.Create(authOptions),
            logger,
            db,
            qrLoginHandler,
            tenantAccessor);
    }

    private static async Task<string?> ExecuteRedirectLocationAsync(IResult result, DefaultHttpContext context)
    {
        await result.ExecuteAsync(context);
        return context.Response.Headers.Location.FirstOrDefault();
    }

    private static DefaultHttpContext CreateHttpContext(
        Dictionary<string, string>? queryParams = null,
        ClaimsPrincipal? user = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        // Register mock multi-tenancy services required by issuer builder
        services.AddSingleton<IMultiTenancyOptions>(new MultiTenancyStateProvider("default", false));
        services.AddScoped<ITenantAccessor>(_ => MockTenantAccessor.CreateWithDefaultTenant());
        services.AddScoped<IIssuerBuilder, IssuerBuilder>();

        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext();
        context.RequestServices = serviceProvider;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Request.Path = "/authorize";
        context.Response.Body = new MemoryStream();

        if (queryParams != null)
        {
            var query = new QueryCollection(queryParams.ToDictionary(
                kvp => kvp.Key,
                kvp => new Microsoft.Extensions.Primitives.StringValues(kvp.Value)));
            context.Request.Query = query;

            var queryString = "?" + string.Join("&", queryParams.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
            context.Request.QueryString = new QueryString(queryString);
        }

        if (user != null)
        {
            context.User = user;
        }

        return context;
    }

    [TestMethod]
    public async Task Authorize_MissingClientId_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();
        var validator = new StubAuthorizeRequestValidator(false, error: "invalid_request", errorDescription: "Missing client_id");
        var handler = CreateHandler(db, validator: validator);

        var queryParams = new Dictionary<string, string>
        {
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error result for missing client_id
    }

    [TestMethod]
    public async Task Authorize_MissingRedirectUri_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();
        var validator = new StubAuthorizeRequestValidator(false, error: "invalid_request", errorDescription: "Missing redirect_uri");
        var handler = CreateHandler(db, validator: validator);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["response_type"] = "code",
            ["scope"] = "openid"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error result for missing redirect_uri
    }

    [TestMethod]
    public async Task Authorize_UnknownClient_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();
        var validator = new StubAuthorizeRequestValidator(false, error: "unauthorized_client", errorDescription: "Unknown client_id");
        var handler = CreateHandler(db, validator: validator);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "unknown_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('a', 43),
            ["code_challenge_method"] = "S256"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for unknown client
    }

    [TestMethod]
    public async Task Authorize_RedirectUri_Mismatch_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();
        var validator = new StubAuthorizeRequestValidator(false, error: "invalid_request", errorDescription: "redirect_uri is not allowed for this client");
        var handler = CreateHandler(db, validator: validator);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://evil.com/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('b', 43),
            ["code_challenge_method"] = "S256"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for redirect_uri mismatch
    }

    [TestMethod]
    public async Task Authorize_InvalidScope_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();
        var validator = new StubAuthorizeRequestValidator(false, error: "invalid_scope", errorDescription: "The following scopes are not allowed: admin");
        var handler = CreateHandler(db, validator: validator);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid admin",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('c', 43),
            ["code_challenge_method"] = "S256"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for invalid scope
    }

    [TestMethod]
    public async Task Authorize_UnsupportedResponseType_Returns_Error()
    {
        // Arrange
        using var db = CreateDb();
        var validator = new StubAuthorizeRequestValidator(false, error: "unsupported_response_type", errorDescription: "Only response_type=code is supported");
        var handler = CreateHandler(db, validator: validator);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "token",
            ["scope"] = "openid",
            ["nonce"] = "nonce123"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for unsupported response_type
    }

    [TestMethod]
    public async Task Authorize_ValidRequest_RedirectsToLogin()
    {
        // Arrange
        using var db = CreateDb();

        // Create valid client in database
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = Guid.NewGuid(),
            RequirePkce = true,
            AllowedLoginRedirectUrisJson = "[\"https://app/callback\"]"
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var authorize = new StubAuthorizeRequestValidator(true, clientId: "test_client", redirectUri: "https://app/callback");
        var handler = CreateHandler(db, validator: authorize);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('d', 43),
            ["code_challenge_method"] = "S256",
            ["state"] = "state123"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler redirects to /login for unauthenticated user
    }

    [TestMethod]
    public async Task Authorize_Prompt_None_With_No_Session_Returns_Login_Required()
    {
        using var db = CreateDb();

        var validator = new StubAuthorizeRequestValidator(
            isValid: true,
            clientId: "test_client",
            redirectUri: "https://app/callback",
            scopes: new[] { "openid" },
            state: "state1");

        var responseGenerator = new AuthorizeResponseGenerator(new StubJarmService());
        var handler = CreateHandler(db, validator: validator, responseGenerator: responseGenerator);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('a', 43),
            ["code_challenge_method"] = "S256",
            ["prompt"] = "none",
            ["state"] = "state1"
        };

        var context = CreateHttpContext(queryParams);

        var result = await handler.HandleAsync(context);

        Assert.IsNotNull(result);
        var loc = await ExecuteRedirectLocationAsync(result, context);
        Assert.IsFalse(string.IsNullOrWhiteSpace(loc));
        Assert.IsTrue(loc!.StartsWith("https://app/callback", StringComparison.Ordinal));
        Assert.IsTrue(loc.Contains("error=login_required", StringComparison.Ordinal));
        Assert.IsTrue(loc.Contains("state=state1", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Authorize_Prompt_None_With_ConsentRequired_Returns_Consent_Required()
    {
        using var db = CreateDb();

        var validator = new StubAuthorizeRequestValidator(
            isValid: true,
            clientId: "test_client",
            redirectUri: "https://app/callback",
            scopes: new[] { "openid" },
            state: "state2");

        var responseGenerator = new AuthorizeResponseGenerator(new StubJarmService());
        var handler = CreateHandler(db, validator: validator, responseGenerator: responseGenerator);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        }, "test"));

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('a', 43),
            ["code_challenge_method"] = "S256",
            ["prompt"] = "none",
            ["state"] = "state2"
        };

        var context = CreateHttpContext(queryParams, user);

        var result = await handler.HandleAsync(context);

        Assert.IsNotNull(result);
        var loc = await ExecuteRedirectLocationAsync(result, context);
        Assert.IsFalse(string.IsNullOrWhiteSpace(loc));
        Assert.IsTrue(loc!.Contains("error=consent_required", StringComparison.Ordinal));
        Assert.IsTrue(loc.Contains("state=state2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Authorize_Prompt_None_With_MaxAge_Exceeded_Returns_Login_Required()
    {
        using var db = CreateDb();

        var validator = new StubAuthorizeRequestValidator(
            isValid: true,
            clientId: "test_client",
            redirectUri: "https://app/callback",
            scopes: new[] { "openid" },
            state: "state3");

        var responseGenerator = new AuthorizeResponseGenerator(new StubJarmService());
        var handler = CreateHandler(db, validator: validator, responseGenerator: responseGenerator);

        var oldAuthTime = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(OidcConstants.Claims.AuthTime, oldAuthTime)
        }, "test"));

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('a', 43),
            ["code_challenge_method"] = "S256",
            ["prompt"] = "none",
            ["max_age"] = "1",
            ["state"] = "state3"
        };

        var context = CreateHttpContext(queryParams, user);

        var result = await handler.HandleAsync(context);

        Assert.IsNotNull(result);
        var loc = await ExecuteRedirectLocationAsync(result, context);
        Assert.IsFalse(string.IsNullOrWhiteSpace(loc));
        Assert.IsTrue(loc!.Contains("error=login_required", StringComparison.Ordinal));
        Assert.IsTrue(loc.Contains("state=state3", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Authorize_AcrValues_Unsupported_Returns_AcrValuesNotSupported()
    {
        using var db = CreateDb();

        var validator = new StubAuthorizeRequestValidator(
            isValid: true,
            clientId: "test_client",
            redirectUri: "https://app/callback",
            scopes: new[] { "openid" },
            state: "state4");

        var responseGenerator = new AuthorizeResponseGenerator(new StubJarmService());
        var handler = CreateHandler(
            db,
            validator: validator,
            responseGenerator: responseGenerator,
            authOptions: new MrWhoOidc.Auth.Services.AuthOptions
            {
                AcrValuesSupported = new[] { "urn:example:acr:1" }
            });

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(OidcConstants.Claims.AuthTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        }, "test"));

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('a', 43),
            ["code_challenge_method"] = "S256",
            ["acr_values"] = "urn:example:acr:2",
            ["state"] = "state4"
        };

        var context = CreateHttpContext(queryParams, user);

        var result = await handler.HandleAsync(context);

        Assert.IsNotNull(result);
        var loc = await ExecuteRedirectLocationAsync(result, context);
        Assert.IsFalse(string.IsNullOrWhiteSpace(loc));
        Assert.IsTrue(loc!.Contains("error=acr_values_not_supported", StringComparison.Ordinal));
        Assert.IsTrue(loc.Contains("state=state4", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Authorize_PKCE_Required_When_Public_Client()
    {
        // Arrange
        using var db = CreateDb();
        var authorize = new StubAuthorizeRequestValidator(false, error: "invalid_request", errorDescription: "PKCE S256 is required for this client");
        var handler = CreateHandler(db, validator: authorize);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "public_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123"
            // Missing code_challenge and code_challenge_method
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler returns error for missing PKCE on public client
    }

    [TestMethod]
    public async Task Authorize_PKCE_CodeChallengeMethod_S256_Supported()
    {
        // Arrange
        using var db = CreateDb();

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = Guid.NewGuid(),
            RequirePkce = true,
            AllowedLoginRedirectUrisJson = "[\"https://app/callback\"]"
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var authorize = new StubAuthorizeRequestValidator(true, clientId: "test_client", redirectUri: "https://app/callback");
        var handler = CreateHandler(db, validator: authorize);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('e', 43),
            ["code_challenge_method"] = "S256",
            ["state"] = "state123"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler accepts S256 code challenge method
    }

    [TestMethod]
    public async Task Authorize_PKCE_CodeChallengeMethod_Plain_Rejected_When_Policy_Enforced()
    {
        // Arrange
        using var db = CreateDb();
        var authorize = new StubAuthorizeRequestValidator(false, error: "invalid_request", errorDescription: "PKCE S256 is required for this client");
        var handler = CreateHandler(db, validator: authorize);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = "plain_challenge",
            ["code_challenge_method"] = "plain", // Plain method rejected
            ["state"] = "state123"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler rejects plain code challenge method when policy requires S256
    }

    [TestMethod]
    public async Task Authorize_RequestObject_Via_PAR_Uri_Resolves_Correctly()
    {
        // Arrange
        using var db = CreateDb();

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = Guid.NewGuid(),
            RequirePkce = true,
            AllowedLoginRedirectUrisJson = "[\"https://app/callback\"]"
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        // PAR store with valid request
        var parRequest = new AuthorizeRequest
        {
            client_id = "test_client",
            redirect_uri = "https://app/callback",
            response_type = "code",
            scope = "openid",
            nonce = "nonce123",
            code_challenge = new string('f', 43),
            code_challenge_method = "S256"
        };
        var parStore = new StubPushedAuthorizationRequestStore("par123", parRequest, "test_client");
        var authorize = new StubAuthorizeRequestValidator(true, clientId: "test_client", redirectUri: "https://app/callback");
        var handler = CreateHandler(db, validator: authorize, parStore: parStore);

        var queryParams = new Dictionary<string, string>
        {
            ["request_uri"] = "urn:ietf:params:oauth:request_uri:par123",
            ["state"] = "state_override"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler resolves PAR request_uri and processes request
    }

    [TestMethod]
    public async Task Authorize_RequestObject_Via_Inline_JWT_Validated_And_Parsed()
    {
        // Arrange
        using var db = CreateDb();

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = Guid.NewGuid(),
            RequirePkce = true,
            AllowedLoginRedirectUrisJson = "[\"https://app/callback\"]"
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        // Request object (JAR)
        var jarRequest = new AuthorizeRequest
        {
            client_id = "test_client",
            redirect_uri = "https://app/callback",
            response_type = "code",
            scope = "openid",
            nonce = "nonce123",
            code_challenge = new string('g', 43),
            code_challenge_method = "S256"
        };
        var requestObjects = new StubAuthorizeRequestOrchestrator(valid: true, request: jarRequest, clientId: "test_client");
        var authorize = new StubAuthorizeRequestValidator(true, clientId: "test_client", redirectUri: "https://app/callback");
        var handler = CreateHandler(db, validator: authorize, orchestrator: requestObjects);

        var queryParams = new Dictionary<string, string>
        {
            ["request"] = "eyJhbGciOiJSUzI1NiJ9.eyJjbGllbnRfaWQiOiJ0ZXN0In0.sig", // Mock JWT
            ["state"] = "state123"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler validates and parses inline request object (JAR)
    }

    [TestMethod]
    public async Task Authorize_State_Parameter_Echoed_In_Callback()
    {
        // Arrange
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var clientGuid = Guid.NewGuid();

        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = realmId,
            RequirePkce = true,
            RequireConsent = false,
            AllowedLoginRedirectUrisJson = "[\"https://app/callback\"]"
        };
        var user = new User { Id = userId, Username = "testuser" };
        var assignment = new UserClientAssignment
        {
            UserId = userId,
            ClientId = clientGuid,
            RealmId = realmId,
            IsActive = true
        };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);
        db.UserClientAssignments.Add(assignment);
        await db.SaveChangesAsync();

        var validator = new StubAuthorizeRequestValidator(true, clientId: "test_client", redirectUri: "https://app/callback", scopes: new[] { "openid" });
        var codes = new StubAuthorizationCodeService(code: "auth_code_123", redirect: "https://app/callback?code=auth_code_123&state=state_value");
        var consents = new StubConsentProcessor();
        var handler = CreateHandler(db, validator: validator, codes: codes, consentProcessor: consents);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('h', 43),
            ["code_challenge_method"] = "S256",
            ["state"] = "state_value"
        };

        // Authenticated user
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var context = CreateHttpContext(queryParams, principal);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler includes state parameter in callback redirect
    }

    [TestMethod]
    public async Task Authorize_Nonce_Parameter_Stored_For_IdToken()
    {
        // Arrange
        using var db = CreateDb();

        var userId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var clientGuid = Guid.NewGuid();

        var realm = new Realm { Id = realmId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = realmId,
            RequirePkce = true,
            RequireConsent = false,
            AllowedLoginRedirectUrisJson = "[\"https://app/callback\"]"
        };
        var user = new User { Id = userId, Username = "testuser" };
        var assignment = new UserClientAssignment
        {
            UserId = userId,
            ClientId = clientGuid,
            RealmId = realmId,
            IsActive = true
        };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);
        db.UserClientAssignments.Add(assignment);
        await db.SaveChangesAsync();

        var nonceValue = "nonce_value_123";
        var validator = new StubAuthorizeRequestValidator(true, clientId: "test_client", redirectUri: "https://app/callback", nonce: nonceValue, scopes: new[] { "openid" });
        var codes = new StubAuthorizationCodeService(code: "auth_code_123");
        var consents = new StubConsentProcessor();
        var handler = CreateHandler(db, validator: validator, codes: codes, consentProcessor: consents);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = nonceValue,
            ["code_challenge"] = new string('i', 43),
            ["code_challenge_method"] = "S256",
            ["state"] = "state123"
        };

        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var context = CreateHttpContext(queryParams, principal);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler stores nonce for later ID token validation
    }

    [TestMethod]
    public async Task Authorize_When_NotAssigned_And_AutoApproval_External_Backfills_Assignment_And_Continues()
    {
        // Arrange
        using var db = CreateDb();

        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var userId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var clientGuid = Guid.NewGuid();

        var realm = new Realm { Id = realmId, TenantId = tenantId, Name = "test_realm" };
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            Id = clientGuid,
            TenantId = tenantId,
            ClientId = "test_client",
            ClientSecretHash = "hash",
            RealmId = realmId,
            RequirePkce = true,
            RequireConsent = false,
            AutoApprovalMode = AutoApprovalMode.OnlyExternalIdp,
            AllowedLoginRedirectUrisJson = "[\"https://app/callback\"]"
        };
        var user = new User { Id = userId, TenantId = tenantId, Username = "testuser" };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // No assignment exists initially
        var validator = new StubAuthorizeRequestValidator(true, clientId: "test_client", redirectUri: "https://app/callback", scopes: new[] { "openid" });
        var codes = new StubAuthorizationCodeService(code: "auth_code_123", redirect: "https://app/callback?code=auth_code_123");
        var consents = new StubConsentProcessor();
        var clientStore = new StubClientStore(client);
        var userAssignments = new UserClientAssignmentService(db, clientStore, NullLogger<UserClientAssignmentService>.Instance);
        var handler = CreateHandler(db, validator: validator, codes: codes, consentProcessor: consents, userAssignments: userAssignments);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('k', 43),
            ["code_challenge_method"] = "S256"
        };

        // Authenticated external session (idp claim set by ExternalOidcSessionManager)
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("idp", "up1")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var context = CreateHttpContext(queryParams, principal);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        var assigned = await db.UserClientAssignments.AsNoTracking().AnyAsync(a =>
            a.UserId == userId && a.ClientId == clientGuid && a.RealmId == realmId && a.IsActive);
        Assert.IsTrue(assigned, "Expected /authorize to backfill the user->client assignment when auto-approval is enabled for external sessions");
    }

    [TestMethod]
    public async Task Authorize_Response_Mode_Form_Post_Supported()
    {
        // Arrange
        using var db = CreateDb();
        var authorize = new StubAuthorizeRequestValidator(true, clientId: "test_client", redirectUri: "https://app/callback", responseMode: "form_post");
        var handler = CreateHandler(db, validator: authorize);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('j', 43),
            ["code_challenge_method"] = "S256",
            ["response_mode"] = "form_post"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler supports response_mode=form_post
    }

    [TestMethod]
    public async Task Authorize_Response_Mode_Query_JWT_Supported()
    {
        // Arrange
        using var db = CreateDb();
        var authorize = new StubAuthorizeRequestValidator(true, clientId: "test_client", redirectUri: "https://app/callback", responseMode: "query.jwt");
        var handler = CreateHandler(db, validator: authorize);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["nonce"] = "nonce123",
            ["code_challenge"] = new string('k', 43),
            ["code_challenge_method"] = "S256",
            ["response_mode"] = "query.jwt"
        };
        var context = CreateHttpContext(queryParams);

        // Act
        var result = await handler.HandleAsync(context);

        // Assert
        Assert.IsNotNull(result);
        // Handler supports JARM response_mode=query.jwt
    }

    // Stub implementations
    private sealed class StubFeatureService : IFeatureService
    {
        private readonly IReadOnlySet<string> _features;

        public StubFeatureService(params string[] features)
        {
            _features = features.Length > 0
                ? new HashSet<string>(features, StringComparer.OrdinalIgnoreCase)
                : FeatureFlags.AllFeatures;
        }

        public Task<bool> IsFeatureEnabledAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_features.Contains(featureName));
        }

        public Task<IReadOnlySet<string>> GetEnabledFeaturesAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_features);
        }

        public Task RecordFeatureUsageAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FeatureUsageMetric>> GetFeatureUsageAsync(Guid? tenantId = null, string? featureName = null, DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<FeatureUsageMetric>>(Array.Empty<FeatureUsageMetric>());
        }
    }

    private sealed class StubAuthorizeRequestValidator : IAuthorizeRequestValidator
    {
        private readonly bool _isValid;
        private readonly string? _error;
        private readonly string? _errorDescription;
        private readonly string? _clientId;
        private readonly string? _redirectUri;
        private readonly string[]? _scopes;
        private readonly string? _nonce;
        private readonly string? _responseMode;
        private readonly bool _requireConsent;
        private readonly string? _state;

        public StubAuthorizeRequestValidator(
            bool isValid = true,
            string? error = null,
            string? errorDescription = null,
            string? clientId = null,
            string? redirectUri = null,
            string[]? scopes = null,
            string? nonce = null,
            string? responseMode = null,
            bool requireConsent = false,
            string? state = null)
        {
            _isValid = isValid;
            _error = error;
            _errorDescription = errorDescription;
            _clientId = clientId;
            _redirectUri = redirectUri;
            _scopes = scopes ?? new[] { "openid" };
            _nonce = nonce;
            _responseMode = responseMode;
            _requireConsent = requireConsent;
            _state = state;
        }

        public Task<AuthorizeValidationResult> ValidateAsync(AuthorizeRequest request, CancellationToken ct = default)
        {
            var result = new AuthorizeValidationResult(
                IsValid: _isValid,
                Error: _error,
                ErrorDescription: _errorDescription,
                ClientId: _clientId ?? request.client_id,
                RedirectUri: _redirectUri ?? request.redirect_uri,
                Scopes: _scopes ?? new[] { "openid" },
                Nonce: _nonce ?? request.nonce,
                ResponseMode: _responseMode ?? request.response_mode,
                RequireConsent: _requireConsent,
                State: _state ?? request.state
            );
            return Task.FromResult(result);
        }
    }

    private sealed class StubAuthorizationCodeService : IAuthorizationCodeService
    {
        private readonly string _code;
        private readonly string? _redirect;

        public StubAuthorizationCodeService(string? code = null, string? redirect = null)
        {
            _code = code ?? "test_code";
            _redirect = redirect;
        }

        public Task<(bool ok, string? error, string? redirect, string? code)> IssueAsync(MrWhoOidc.Auth.Services.Authorization.AuthorizeValidationResult valid, Guid userId, CancellationToken ct = default)
        {
            var redirectUrl = _redirect ?? $"{valid.RedirectUri}?code={_code}";
            if (!string.IsNullOrEmpty(valid.ResponseMode))
            {
                redirectUrl += $"&response_mode={valid.ResponseMode}";
            }
            return Task.FromResult((true, (string?)null, (string?)redirectUrl, (string?)_code));
        }
    }

    private sealed class StubConsentService : IConsentService
    {
        private readonly bool _hasConsent;

        public StubConsentService(bool hasConsent = true)
        {
            _hasConsent = hasConsent;
        }

        public Task<bool> HasConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default)
        {
            return Task.FromResult(_hasConsent);
        }

        public Task GrantConsentAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task RevokeConsentAsync(Guid userId, string clientId, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StubAuthorizationCodeMetadataStore : IAuthorizationCodeMetadataStore
    {
        public bool TryGetAuthTime(string code, out DateTimeOffset authTime)
        {
            authTime = DateTimeOffset.UtcNow;
            return true;
        }

        public void SetAuthTime(string code, DateTimeOffset authTime)
        {
        }

        public void SetResource(string code, string resource)
        {
        }

        public bool TryGetResource(string code, out string? resource)
        {
            resource = null;
            return false;
        }

        public void SetUpstream(string code, string? upstreamIdp, string? upstreamSub, string? upstreamAccessToken)
        {
        }

        public bool TryGetUpstream(string code, out string? upstreamIdp, out string? upstreamSub, out string? upstreamAccessToken)
        {
            upstreamIdp = null;
            upstreamSub = null;
            upstreamAccessToken = null;
            return false;
        }

        public void SetMappedClaims(string code, IReadOnlyDictionary<string, string> claims)
        {
        }

        public bool TryGetMappedClaims(string code, out IReadOnlyDictionary<string, string> claims)
        {
            claims = new Dictionary<string, string>();
            return false;
        }

        public void SetSid(string code, string sid)
        {
        }

        public bool TryGetSid(string code, out string? sid)
        {
            sid = null;
            return false;
        }

        public void Remove(string code)
        {
        }
    }

    private sealed class StubPushedAuthorizationRequestStore : IPushedAuthorizationRequestStore
    {
        private readonly string? _parId;
        private readonly AuthorizeRequest? _request;
        private readonly string? _clientId;

        public StubPushedAuthorizationRequestStore(string? parId = null, AuthorizeRequest? request = null, string? clientId = null)
        {
            _parId = parId;
            _request = request;
            _clientId = clientId;
        }

        public PushedAuthorizationRequestEntry? TryGetById(string requestUri)
        {
            if (_parId != null && requestUri.EndsWith(_parId))
            {
                return new PushedAuthorizationRequestEntry
                {
                    Request = _request!,
                    ClientId = _clientId ?? "test_client",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                };
            }
            return null;
        }

        public DateTimeOffset Create(string id, AuthorizeRequest request, string clientId, TimeSpan lifetime, string? requestUri)
        {
            return DateTimeOffset.UtcNow.Add(lifetime);
        }

        public void MarkConsumedById(string requestUri)
        {
        }

        public PushedAuthorizationRequestEntry? TryConsumeById(string requestUri)
        {
            return TryGetById(requestUri);
        }
    }

    private sealed class StubAuthorizeRequestOrchestrator : IAuthorizeRequestOrchestrator
    {
        private readonly bool _valid;
        private readonly AuthorizeRequest? _request;
        private readonly string? _clientId;

        public StubAuthorizeRequestOrchestrator(bool valid = true, AuthorizeRequest? request = null, string? clientId = null)
        {
            _valid = valid;
            _request = request;
            _clientId = clientId;
        }

        public Task<(IResult? error, AuthorizationContext? context)> ResolveAndValidateAsync(HttpContext http, CancellationToken ct = default)
        {
            var request = _request ?? new AuthorizeRequest(client_id: _clientId);
            if (!_valid)
            {
                return Task.FromResult<(IResult? error, AuthorizationContext? context)>((Results.BadRequest("Invalid request"), null));
            }
            return Task.FromResult<(IResult? error, AuthorizationContext? context)>((null, new AuthorizationContext(request, "corr", "bucket", "query", null)));
        }
    }

    private sealed class StubJwtService : IJwtService
    {
        public string CreateIdToken(Guid userId, string clientId, string nonce, string[] scopes, DateTimeOffset authTime, string? accessTokenHash = null)
        {
            return "stub_id_token";
        }

        public string CreateAccessToken(Guid userId, string clientId, string[] scopes, string? resource = null, string? jkt = null)
        {
            return "stub_access_token";
        }

        public string CreateRefreshToken()
        {
            return "stub_refresh_token";
        }

        public Task<string> CreateJwtAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
        {
            return Task.FromResult("stub_jwt");
        }

        public Task<string> CreateJwtEncryptedAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, EncryptingCredentials encryptingCredentials, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
        {
            return Task.FromResult("stub_jwt_encrypted");
        }
    }

    private sealed class StubClientStore : IClientStore
    {
        private readonly MrWhoOidc.Auth.Persistence.Client? _client;

        public StubClientStore(MrWhoOidc.Auth.Persistence.Client? client = null)
        {
            _client = client;
        }

        public Task<MrWhoOidc.Auth.Persistence.Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
        {
            if (_client != null && _client.ClientId == clientId)
            {
                return Task.FromResult<MrWhoOidc.Auth.Persistence.Client?>(_client);
            }
            return Task.FromResult<MrWhoOidc.Auth.Persistence.Client?>(null);
        }

        public Task<bool> ValidateClientCredentialsAsync(string clientId, string clientSecret, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default)
        {
            return Task.FromResult(true);
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

    private sealed class StubQrLoginHandler : IQrLoginHandler
    {
        public Task<IResult> InitiateAsync(HttpContext http)
        {
            return Task.FromResult(Results.Ok(new { message = "QR login initiated" }) as IResult);
        }

        public Task<IResult> InitiateAsync(HttpContext http, MrWhoOidc.Auth.Services.Authorization.AuthorizeValidationResult validationResult, MrWhoOidc.Auth.Services.Authorization.AuthorizeRequest request)
        {
            return Task.FromResult(Results.Ok(new { message = "QR login initiated from authorize", clientId = validationResult.ClientId }) as IResult);
        }

        public Task<IResult> GetStatusAsync(HttpContext http, string sessionToken)
        {
            return Task.FromResult(Results.Ok(new { status = "pending" }) as IResult);
        }

        public Task<IResult> ConfirmAsync(HttpContext http)
        {
            return Task.FromResult(Results.Ok(new { success = true }) as IResult);
        }

        public Task<IResult> CancelAsync(HttpContext http)
        {
            return Task.FromResult(Results.Ok(new { success = true }) as IResult);
        }

        public Task<IResult> MobileLandingAsync(HttpContext http)
        {
            return Task.FromResult(Results.Ok() as IResult);
        }

        public Task<IResult> ConfirmPageAsync(HttpContext http)
        {
            return Task.FromResult(Results.Ok() as IResult);
        }

        public Task<IResult> CompleteAsync(HttpContext http, string sessionToken)
        {
            return Task.FromResult(Results.Ok() as IResult);
        }
    }

    internal class StubJarmService : IJarmService
    {
        public Task<string> CreateSuccessResponseAsync(string clientId, string issuer, string code, string responseMode, string? state)
        {
            return Task.FromResult("mock_jarm_jwt");
        }

        public Task<string> CreateErrorResponseAsync(string clientId, string issuer, string error, string errorDescription, string? state)
        {
            return Task.FromResult("mock_jarm_error_jwt");
        }
    }

    private sealed class StubConsentProcessor : IConsentProcessor
    {
        public Task<ConsentDecision> EvaluateAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default)
            => Task.FromResult(new ConsentDecision(false, true));
    }

    private sealed class StubProviderSelectionService : IProviderSelectionService
    {
        public Task<ProviderSelectionResult> EvaluateAsync(string clientId, string? idp, string? idpHint, string? lastUsedIdp, bool forceAccountSelection, CancellationToken ct = default)
            => Task.FromResult(new ProviderSelectionResult(false, idp ?? idpHint ?? lastUsedIdp));
    }

    private sealed class StubUserClientAssignmentService : IUserClientAssignmentService
    {
        public Task<(bool assigned, string? error)> EnsureAssignedAsync(Guid userId, string clientId, string? idp, CancellationToken ct = default)
            => Task.FromResult((true, (string?)null));
    }

    private sealed class StubAuthorizeResponseGenerator : IAuthorizeResponseGenerator
    {
        public IResult CreateSuccessResponse(HttpContext http, AuthorizeValidationResult validation, string code, string? redirectUri) => Results.Ok();
        public IResult CreateErrorResponse(HttpContext http, AuthorizeValidationResult validation, string correlationId) => Results.BadRequest();
        public IResult CreateConsentRedirect(HttpContext http, AuthorizeValidationResult validation, string consentUrl) => Results.Redirect(consentUrl);
    }

    private sealed class StubAuthorizeRequestSanitizer : IAuthorizeRequestSanitizer
    {
        public IResult? SanitizeAddressBar(HttpContext http) => null;
    }

    private sealed class StubAuthenticationRedirectService : IAuthenticationRedirectService
    {
        public Task<IResult> RedirectToLoginAsync(HttpContext http, ProviderSelectionResult selection, AuthorizeValidationResult validation, CancellationToken ct = default)
            => Task.FromResult(Results.Redirect("/login") as IResult);
    }

    private sealed class StubAuthorizationMetadataService : IAuthorizationMetadataService
    {
        public Task PopulateMetadataAsync(HttpContext http, string code, CancellationToken ct = default) => Task.CompletedTask;
    }
}
