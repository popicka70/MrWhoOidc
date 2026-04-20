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
        IRequestObjectValidator? requestObjects = null)
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
            NullLogger<AuthorizeRequestResolver>.Instance);
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

        public void MarkConsumedById(string id) { }

        public PushedAuthorizationRequestEntry? TryConsumeById(string id) => TryGetById(id);
    }

    private sealed class StubRequestObjectValidator : IRequestObjectValidator
    {
        public Task<RequestObjectValidationResult> ValidateAsync(string requestJwt, string audience, System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult(new RequestObjectValidationResult { IsValid = false, Error = "invalid_request_object" });
        }
    }
}
