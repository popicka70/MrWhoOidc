using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Regression tests proving that OIDC authorize parameters (claims, max_age, acr_values,
/// display, ui_locales, prompt, id_token_hint, login_hint) survive the full resolver path
/// for plain query, PAR, and JAR requests.
/// Finding 1 from the 2026-03-10 OIDC spec compliance assessment.
/// </summary>
[TestClass]
public sealed class AuthorizeRequestResolverOidcParamTests
{
    private static AuthorizeRequestResolver CreateResolver(
        IPushedAuthorizationRequestStore? parStore = null,
        IRequestObjectValidator? requestObjects = null,
        RequestObjectUriFetcher? requestUriFetcher = null)
    {
        var db = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        return new AuthorizeRequestResolver(
            requestObjects ?? new StubRequestObjectValidator(),
            parStore ?? new StubParStore(),
            db,
            Options.Create(new AuthOptions()),
            NullLogger<AuthorizeRequestResolver>.Instance,
            requestUriFetcher);
    }

    private static IEnumerable<KeyValuePair<string, string>> BaseQueryParams(
        Dictionary<string, string>? extra = null)
    {
        var ps = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "test_client",
            ["redirect_uri"] = "https://app/callback",
            ["scope"] = "openid",
            ["state"] = "s1",
            ["nonce"] = "n1",
            ["code_challenge"] = new string('a', 43),
            ["code_challenge_method"] = "S256"
        };
        if (extra != null)
            foreach (var (k, v) in extra) ps[k] = v;
        return ps;
    }

    [TestMethod]
    public async Task Query_Claims_Propagates_Through_Resolver()
    {
        var resolver = CreateResolver();
        var claimsJson = "{\"userinfo\":{\"email\":{\"essential\":true}}}";
        var query = BaseQueryParams(new() { ["claims"] = claimsJson });

        var result = await resolver.ResolveAsync(query, null, null, "https://op.example.com");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual(claimsJson, result.Request!.claims);
    }

    [TestMethod]
    public async Task Query_MaxAge_Propagates_Through_Resolver()
    {
        var resolver = CreateResolver();
        var query = BaseQueryParams(new() { ["max_age"] = "3600" });

        var result = await resolver.ResolveAsync(query, null, null, "https://op.example.com");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("3600", result.Request!.max_age);
    }

    [TestMethod]
    public async Task Query_AcrValues_Propagates_Through_Resolver()
    {
        var resolver = CreateResolver();
        var query = BaseQueryParams(new() { ["acr_values"] = "urn:mfa urn:sms" });

        var result = await resolver.ResolveAsync(query, null, null, "https://op.example.com");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("urn:mfa urn:sms", result.Request!.acr_values);
    }

    [TestMethod]
    public async Task Query_Display_Propagates_Through_Resolver()
    {
        var resolver = CreateResolver();
        var query = BaseQueryParams(new() { ["display"] = "popup" });

        var result = await resolver.ResolveAsync(query, null, null, "https://op.example.com");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("popup", result.Request!.display);
    }

    [TestMethod]
    public async Task Query_UiLocales_Propagates_Through_Resolver()
    {
        var resolver = CreateResolver();
        var query = BaseQueryParams(new() { ["ui_locales"] = "en-US fr" });

        var result = await resolver.ResolveAsync(query, null, null, "https://op.example.com");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("en-US fr", result.Request!.ui_locales);
    }

    [TestMethod]
    public async Task Query_Prompt_Propagates_Through_Resolver()
    {
        var resolver = CreateResolver();
        var query = BaseQueryParams(new() { ["prompt"] = "login" });

        var result = await resolver.ResolveAsync(query, null, null, "https://op.example.com");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("login", result.Request!.prompt);
    }

    [TestMethod]
    public async Task Query_LoginHint_Propagates_Through_Resolver()
    {
        var resolver = CreateResolver();
        var query = BaseQueryParams(new() { ["login_hint"] = "user@example.com" });

        var result = await resolver.ResolveAsync(query, null, null, "https://op.example.com");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("user@example.com", result.Request!.login_hint);
    }

    [TestMethod]
    public async Task Query_IdTokenHint_Propagates_Through_Resolver()
    {
        var resolver = CreateResolver();
        var query = BaseQueryParams(new() { ["id_token_hint"] = "eyJhbGciOiJSUzI1NiJ9.stub" });

        var result = await resolver.ResolveAsync(query, null, null, "https://op.example.com");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("eyJhbGciOiJSUzI1NiJ9.stub", result.Request!.id_token_hint);
    }

    [TestMethod]
    public async Task AllOidcParams_Propagate_Together()
    {
        var resolver = CreateResolver();
        var claimsJson = "{\"userinfo\":{\"email\":null}}";
        var query = BaseQueryParams(new()
        {
            ["claims"] = claimsJson,
            ["max_age"] = "900",
            ["acr_values"] = "urn:mfa",
            ["display"] = "popup",
            ["ui_locales"] = "de",
            ["prompt"] = "consent",
            ["login_hint"] = "alice@example.com"
        });

        var result = await resolver.ResolveAsync(query, null, null, "https://op.example.com");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        var req = result.Request!;
        Assert.AreEqual(claimsJson, req.claims, "claims");
        Assert.AreEqual("900", req.max_age, "max_age");
        Assert.AreEqual("urn:mfa", req.acr_values, "acr_values");
        Assert.AreEqual("popup", req.display, "display");
        Assert.AreEqual("de", req.ui_locales, "ui_locales");
        Assert.AreEqual("consent", req.prompt, "prompt");
        Assert.AreEqual("alice@example.com", req.login_hint, "login_hint");
    }

    [TestMethod]
    public async Task PAR_Request_Preserves_OidcParams()
    {
        // An AuthorizeRequest stored via PAR already contains OIDC params.
        // The resolver must return them unchanged.
        var parRequest = new AuthorizeRequest(
            response_type: "code",
            client_id: "test_client",
            redirect_uri: "https://app/callback",
            scope: "openid",
            state: "s1",
            nonce: "n1",
            code_challenge: new string('b', 43),
            code_challenge_method: "S256",
            claims: "{\"userinfo\":{\"email\":null}}",
            max_age: "600",
            acr_values: "urn:mfa",
            display: "popup",
            prompt: "login"
        );

        var parStore = new StubParStore(entry: parRequest);
        var resolver = CreateResolver(parStore: parStore);

        // Query passes only client_id, state, and the request_uri pointing to the stored PAR
        var query = new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["state"] = "s1"
        };

        var result = await resolver.ResolveAsync(query, "urn:ietf:params:oauth:request_uri:stub-par-id", null, "https://op.example.com");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("par", result.Mode);
        var req = result.Request!;
        Assert.AreEqual("{\"userinfo\":{\"email\":null}}", req.claims, "claims");
        Assert.AreEqual("600", req.max_age, "max_age");
        Assert.AreEqual("urn:mfa", req.acr_values, "acr_values");
        Assert.AreEqual("popup", req.display, "display");
        Assert.AreEqual("login", req.prompt, "prompt");
    }

    [TestMethod]
    public async Task External_RequestUri_Returns_RequestUriNotSupported()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            BaseQueryParams(),
            "https://client.example.com/request.jwt",
            null,
            "https://op.example.com");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("request_uri_not_supported", result.Error);
    }

    [TestMethod]
    public async Task External_RequestUri_WithFetcher_FetchesAndValidatesTheRequestObject()
    {
        // OIDC Core 6.2 / RFC 9101 §5.5: an external request_uri (not a PAR urn) must be fetched from the
        // client's pre-registered request_uris and validated like an inline request object.
        var db = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        db.Clients.Add(new Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "test_client",
            TenantId = Guid.NewGuid(),
            RequestUrisJson = "[\"https://client.example.com/request.jwt#frag\"]"
        });
        db.SaveChanges();

        var handler = new StubHttpMessageHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("eyJhbGciOiJSUzI1NiJ9.eyJpc3MiOiJ0ZXN0X2NsaWVudCJ9.c2ln")
        });
        var fetcher = new RequestObjectUriFetcher(db, messageHandler: handler);

        // Mirrors the conformance suite's request_uri flow: the request object is a copy of the
        // authorization request, and the query keeps the required duplicates (response_type, client_id,
        // scope, redirect_uri) plus request_uri. Other params live only in the request object.
        var requestObject = new AuthorizeRequest(
            response_type: "code",
            client_id: "test_client",
            redirect_uri: "https://app/callback",
            scope: "openid",
            state: "s1",
            nonce: "n1",
            code_challenge: new string('a', 43),
            code_challenge_method: "S256");
        var validator = new StubRequestObjectValidator(valid: true, clientId: "test_client", request: requestObject);

        var resolver = CreateResolver(requestObjects: validator, requestUriFetcher: fetcher);

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = "test_client",
            ["scope"] = "openid",
            ["redirect_uri"] = "https://app/callback"
        };

        // A different fragment must still match the pre-registered URI (fragment-insensitive comparison).
        var result = await resolver.ResolveAsync(
            query,
            "https://client.example.com/request.jwt#different",
            null,
            "https://op.example.com");

        Assert.IsTrue(result.IsValid, $"{result.Error}: {result.ErrorDescription}");
        Assert.AreEqual("request_uri", result.Mode);
        Assert.IsNotNull(result.Request);
        Assert.AreEqual("test_client", result.ClientId);

        // The fetcher stripped the fragment before the HTTP call.
        Assert.AreEqual("https://client.example.com/request.jwt", handler.RequestedUrl?.AbsoluteUri);
    }

    [TestMethod]
    public async Task External_RequestUri_WithRequest_Both_IsRejected()
    {
        // OIDC Core 15.1 / RFC 9101: request and request_uri are mutually exclusive.
        var db = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        db.Clients.Add(new Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "test_client",
            TenantId = Guid.NewGuid(),
            RequestUrisJson = "[\"https://client.example.com/request.jwt\"]"
        });
        db.SaveChanges();

        var handler = new StubHttpMessageHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("a.b.c")
        });
        var fetcher = new RequestObjectUriFetcher(db, messageHandler: handler);

        var resolver = CreateResolver(requestUriFetcher: fetcher);

        var result = await resolver.ResolveAsync(
            BaseQueryParams(),
            "https://client.example.com/request.jwt",
            "eyJhbGciOiJSUzI1NiJ9.eyJpc3MiOiJ0In9.c2ln",
            "https://op.example.com");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request", result.Error);
    }

    // -----------------------------------------------------------------------
    // Private stubs
    // -----------------------------------------------------------------------

    private sealed class StubParStore : IPushedAuthorizationRequestStore
    {
        private readonly AuthorizeRequest? _entry;

        public StubParStore(AuthorizeRequest? entry = null)
        {
            _entry = entry;
        }

        public DateTimeOffset Create(string id, AuthorizeRequest request, string clientId, TimeSpan lifetime, string? requestUri)
            => DateTimeOffset.UtcNow.Add(lifetime);

        public PushedAuthorizationRequestEntry? TryGetById(string id)
        {
            if (_entry == null) return null;
            return new PushedAuthorizationRequestEntry
            {
                ClientId = _entry.client_id ?? "test_client",
                Request = _entry,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
        }

        public bool MarkConsumedById(string id) => true;

        public PushedAuthorizationRequestEntry? TryConsumeById(string id) => TryGetById(id);
    }

    private sealed class StubRequestObjectValidator : IRequestObjectValidator
    {
        private readonly bool _valid;
        private readonly string? _clientId;
        private readonly AuthorizeRequest? _request;

        public StubRequestObjectValidator(bool valid = false, string? clientId = null, AuthorizeRequest? request = null)
        {
            _valid = valid;
            _clientId = clientId;
            _request = request;
        }

        public Task<RequestObjectValidationResult> ValidateAsync(string requestJwt, string audience, System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult(_valid
                ? new RequestObjectValidationResult { IsValid = true, ClientId = _clientId, Request = _request }
                : new RequestObjectValidationResult { IsValid = false, Error = "invalid_request_object" });
        }
    }

    /// <summary>A handler that records the requested URL and returns a fixed response.</summary>
    private sealed class StubHttpMessageHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> _responder;

        public Uri? RequestedUrl { get; private set; }

        public StubHttpMessageHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            RequestedUrl = request.RequestUri;
            return Task.FromResult(_responder(request));
        }
    }
}
