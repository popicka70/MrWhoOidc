using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Round trips through /authorize -> login/consent -> /authorize for request objects (JAR) and pushed
/// authorization requests (PAR) whose prompt cannot be stripped from the return URL.
/// Uses the real orchestrator, resolver, request object validator and JAR replay cache.
/// </summary>
[TestClass]
public sealed class AuthorizeInteractionResumptionTests
{
    private const string ClientId = "jar-client";
    private const string Issuer = "https://as.example";
    private const string RedirectUri = "https://rp.example/cb";
    private const string ParId = "0f8fad5bd9cb469fa16570867728950e";

    private AuthDbContext _db = null!;
    private JsonWebKey _jwk = null!;
    private IDistributedCache _cache = null!;
    private TrackingLoginRedirect _login = null!;
    private CountingCodeService _codes = null!;
    private StubParStore _par = null!;

    [TestInitialize]
    public async Task Init()
    {
        _db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(true);
        var jwkJson = $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"kid\":\"{Guid.NewGuid():N}\",\"n\":\"{B(p.Modulus)}\",\"e\":\"{B(p.Exponent)}\",\"d\":\"{B(p.D)}\",\"p\":\"{B(p.P)}\",\"q\":\"{B(p.Q)}\",\"dp\":\"{B(p.DP)}\",\"dq\":\"{B(p.DQ)}\",\"qi\":\"{B(p.InverseQ)}\"}}";
        _jwk = new JsonWebKey(jwkJson);
        _db.Clients.Add(new ClientEntity { ClientId = ClientId, PublicJwksJson = jwkJson });
        await _db.SaveChangesAsync();

        _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        _login = new TrackingLoginRedirect();
        _codes = new CountingCodeService();
        _par = new StubParStore(new AuthorizeRequest(
            response_type: "code",
            client_id: ClientId,
            redirect_uri: RedirectUri,
            scope: "openid",
            state: "s1",
            nonce: "n1",
            prompt: "login"));
    }

    [TestCleanup]
    public void Cleanup() => _db.Dispose();

    [TestMethod]
    public async Task Jar_PromptLogin_RoundTrip_Issues_Code_After_ReLogin_And_Rejects_Later_Replay()
    {
        var query = JarQuery(CreateRequestObject(prompt: "login"));

        // 1st pass: an existing (older) session must re-authenticate.
        var first = CreateContext(query, AuthTimeAgo(TimeSpan.FromHours(1)));
        await HandleAsync(first);
        Assert.AreEqual(1, _login.Count, "prompt=login must force the login page");
        Assert.AreEqual(0, _codes.Count);
        var cookie = BindingCookie(first);

        // Return trip after login: same request object, same browser, fresh authentication.
        await HandleAsync(CreateContext(query, AuthTimeAgo(TimeSpan.Zero), cookie));
        Assert.AreEqual(1, _login.Count, "prompt=login must count as satisfied after re-authentication (no loop)");
        Assert.AreEqual(1, _codes.Count, "the resumed request object must not trip the JAR replay cache");

        // The authorization finished: replaying the same request object is a new use and is rejected.
        await HandleAsync(CreateContext(query, AuthTimeAgo(TimeSpan.Zero), cookie));
        Assert.AreEqual(1, _codes.Count, "replay after completion must be rejected");
        Assert.AreEqual(1, _login.Count);
    }

    [TestMethod]
    public async Task Jar_Resumption_Requires_Fresh_Authentication_And_The_Same_Browser()
    {
        var query = JarQuery(CreateRequestObject(prompt: "login"));

        var first = CreateContext(query, AuthTimeAgo(TimeSpan.FromHours(1)));
        await HandleAsync(first);
        var cookie = BindingCookie(first);

        // Same browser, but the session did not re-authenticate: prompt=login is still pending.
        await HandleAsync(CreateContext(query, AuthTimeAgo(TimeSpan.FromHours(1)), cookie));
        Assert.AreEqual(2, _login.Count);
        Assert.AreEqual(0, _codes.Count);

        // Another browser (no binding cookie) using the captured request object: replay-checked and rejected.
        await HandleAsync(CreateContext(query, AuthTimeAgo(TimeSpan.Zero)));
        Assert.AreEqual(2, _login.Count);
        Assert.AreEqual(0, _codes.Count);
    }

    [TestMethod]
    public async Task Jar_Unauthenticated_Login_RoundTrip_Does_Not_Hit_Replay_Detection()
    {
        var query = JarQuery(CreateRequestObject(prompt: null));

        var first = CreateContext(query, user: null);
        await HandleAsync(first);
        Assert.AreEqual(1, _login.Count);

        await HandleAsync(CreateContext(query, AuthTimeAgo(TimeSpan.Zero), BindingCookie(first)));
        Assert.AreEqual(1, _codes.Count);
    }

    [TestMethod]
    public async Task Par_PromptLogin_RoundTrip_Issues_Code_After_ReLogin()
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["request_uri"] = "urn:ietf:params:oauth:request_uri:" + ParId
        };

        var first = CreateContext(query, AuthTimeAgo(TimeSpan.FromHours(1)));
        await HandleAsync(first);
        Assert.AreEqual(1, _login.Count, "prompt=login in the PAR entry must force the login page");

        await HandleAsync(CreateContext(query, AuthTimeAgo(TimeSpan.Zero), BindingCookie(first)));
        Assert.AreEqual(1, _login.Count, "prompt=login in the PAR entry must count as satisfied after re-authentication");
        Assert.AreEqual(1, _codes.Count);
        Assert.AreEqual(1, _par.ConsumeCount);
    }

    [TestMethod]
    public async Task Jar_PromptConsent_RoundTrip_Issues_Code_After_Consent()
    {
        var query = JarQuery(CreateRequestObject(prompt: "consent"));
        var store = new DistributedAuthorizeInteractionStore(_cache);

        var first = CreateContext(query, AuthTimeAgo(TimeSpan.FromHours(1)));
        var result = await HandleAsync(first);
        Assert.IsInstanceOfType<Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult>(result);
        var cookie = BindingCookie(first);

        // The consent page records completion for the request in the return URL.
        var consentPost = CreateContext(new Dictionary<string, string>(), AuthTimeAgo(TimeSpan.FromHours(1)), cookie);
        var returnUrl = "/authorize?client_id=" + ClientId + "&request=" + query["request"];
        await store.MarkConsentGivenAsync(consentPost, AuthorizeInteractionKey.FromReturnUrl(returnUrl)!);

        await HandleAsync(CreateContext(query, AuthTimeAgo(TimeSpan.FromHours(1)), cookie));
        Assert.AreEqual(1, _codes.Count, "prompt=consent must count as satisfied after the consent screen");
    }

    [TestMethod]
    public async Task Jar_Without_Prompt_Ignores_Unsigned_Query_Prompt()
    {
        // RFC 9101: with a request object only its parameters count; an unsigned prompt=login is ignored.
        var query = JarQuery(CreateRequestObject(prompt: null));
        query["prompt"] = "login";

        await HandleAsync(CreateContext(query, AuthTimeAgo(TimeSpan.FromHours(1))));

        Assert.AreEqual(0, _login.Count);
        Assert.AreEqual(1, _codes.Count);
    }

    [TestMethod]
    public void InteractionKey_Distinguishes_Request_Objects_And_Ignores_Query_Requests()
    {
        Assert.IsNull(AuthorizeInteractionKey.From(null, null));
        Assert.IsNull(AuthorizeInteractionKey.FromReturnUrl("/authorize?client_id=x&prompt=login"));
        Assert.IsNull(AuthorizeInteractionKey.FromReturnUrl("https://evil.example/authorize?request=a.b.c"));
        Assert.AreNotEqual(AuthorizeInteractionKey.From(null, "a.b.c"), AuthorizeInteractionKey.From(null, "a.b.d"));
        Assert.AreEqual(AuthorizeInteractionKey.From("urn:x", null), AuthorizeInteractionKey.FromReturnUrl("/t/acme/authorize?client_id=c&request_uri=urn%3Ax"));
    }

    private async Task<IResult> HandleAsync(DefaultHttpContext http)
    {
        var options = Options.Create(new AuthOptions());
        var store = new DistributedAuthorizeInteractionStore(_cache);
        var validator = new RequestObjectValidator(_db, NullLogger<RequestObjectValidator>.Instance, options, new InMemoryJarReplayCache(), new NoopDecryptor());
        var resolver = new AuthorizeRequestResolver(validator, _par, _db, options, NullLogger<AuthorizeRequestResolver>.Instance);
        var orchestrator = new AuthorizeRequestOrchestrator(resolver, options, new OidcEndpointMetrics(), NullLogger<AuthorizeRequestOrchestrator>.Instance, store);

        var tenantAccessor = new MockTenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Slug = "default",
            Name = "Default",
            IssuerUri = Issuer,
            IsMultiTenantMode = false
        });

        var handler = new AuthorizeHandler(
            new PromptMappingValidator(),
            new NoopAuditSink(),
            new NoConsentNeeded(),
            new NoProviderSelection(),
            new AlwaysAssigned(),
            new SimpleResponses(),
            new NoSanitize(),
            _login,
            new NoMetadata(),
            orchestrator,
            _codes,
            new OidcEndpointMetrics(),
            _par,
            options,
            NullLogger<AuthorizeHandler>.Instance,
            _db,
            new NoQr(),
            tenantAccessor,
            store);

        return await handler.HandleAsync(http);
    }

    private static Dictionary<string, string> JarQuery(string requestObject) => new()
    {
        ["client_id"] = ClientId,
        ["request"] = requestObject
    };

    private string CreateRequestObject(string? prompt)
    {
        var claims = new List<Claim>
        {
            new("client_id", ClientId),
            new("response_type", "code"),
            new("redirect_uri", RedirectUri),
            new("scope", "openid"),
            new("state", "s1"),
            new("nonce", "n1"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        if (prompt is not null)
        {
            claims.Add(new Claim("prompt", prompt));
        }

        var token = new JwtSecurityToken(
            issuer: ClientId,
            audience: Issuer + "/authorize",
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(_jwk, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static ClaimsPrincipal AuthTimeAgo(TimeSpan age)
        => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "11111111-1111-1111-1111-111111111111"),
            new Claim(OidcConstants.Claims.AuthTime, DateTimeOffset.UtcNow.Subtract(age).ToUnixTimeSeconds().ToString())
        ], "test"));

    private static DefaultHttpContext CreateContext(Dictionary<string, string> query, ClaimsPrincipal? user, string? cookie = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new OidcOptions { Issuer = Issuer })
            .BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("as.example");
        http.Request.Path = "/authorize";
        http.Request.Query = new QueryCollection(query.ToDictionary(k => k.Key, k => new Microsoft.Extensions.Primitives.StringValues(k.Value)));
        http.Response.Body = new MemoryStream();
        if (cookie is not null)
        {
            http.Request.Headers.Cookie = cookie;
        }

        if (user is not null)
        {
            http.User = user;
        }

        return http;
    }

    private static string BindingCookie(HttpContext http)
    {
        var setCookie = http.Response.Headers.SetCookie.ToString();
        var prefix = DistributedAuthorizeInteractionStore.BindingCookieName + "=";
        var start = setCookie.IndexOf(prefix, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, "an interaction for a JAR/PAR request must bind the browser");
        var end = setCookie.IndexOf(';', start);
        return setCookie[start..(end < 0 ? setCookie.Length : end)];
    }

    private static string B(byte[]? bytes) => Base64UrlEncoder.Encode(bytes);

    private sealed class NoopDecryptor : IRequestObjectDecryptor
    {
        public Task<string?> TryDecryptToInnerJwtAsync(string requestObject, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class PromptMappingValidator : IAuthorizeRequestValidator
    {
        public Task<AuthorizeValidationResult> ValidateAsync(AuthorizeRequest request, CancellationToken ct = default)
            => Task.FromResult(new AuthorizeValidationResult(
                IsValid: true,
                ClientId: request.client_id,
                RedirectUri: request.redirect_uri,
                Scopes: ["openid"],
                Nonce: request.nonce,
                State: request.state,
                PromptValues: string.IsNullOrWhiteSpace(request.prompt) ? null : request.prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    private sealed class TrackingLoginRedirect : IAuthenticationRedirectService
    {
        public int Count { get; private set; }

        public Task<IResult> RedirectToLoginAsync(HttpContext http, ProviderSelectionResult selection, AuthorizeValidationResult validation, string? display = null, CancellationToken ct = default)
        {
            Count++;
            return Task.FromResult(Results.Redirect("/login"));
        }
    }

    private sealed class CountingCodeService : IAuthorizationCodeService
    {
        public int Count { get; private set; }

        public Task<(bool ok, string? error, string? redirect, string? code)> IssueAsync(AuthorizeValidationResult valid, Guid userId, CancellationToken ct = default, DateTimeOffset? authTime = null)
        {
            Count++;
            return Task.FromResult((true, (string?)null, (string?)(valid.RedirectUri + "?code=c"), (string?)"c"));
        }
    }

    private sealed class StubParStore(AuthorizeRequest request) : IPushedAuthorizationRequestStore
    {
        private bool _consumed;

        public int ConsumeCount { get; private set; }

        public PushedAuthorizationRequestEntry? TryGetById(string requestUri)
            => !_consumed && requestUri == ParId
                ? new PushedAuthorizationRequestEntry { ClientId = ClientId, Request = request, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }
                : null;

        public DateTimeOffset Create(string id, AuthorizeRequest request, string clientId, TimeSpan lifetime, string? requestUri) => DateTimeOffset.UtcNow.Add(lifetime);

        public bool MarkConsumedById(string requestUri)
        {
            if (_consumed || requestUri != ParId) return false;
            _consumed = true;
            ConsumeCount++;
            return true;
        }

        public PushedAuthorizationRequestEntry? TryConsumeById(string requestUri)
        {
            var entry = TryGetById(requestUri);
            if (entry is not null) MarkConsumedById(requestUri);
            return entry;
        }
    }

    private sealed class NoConsentNeeded : IConsentProcessor
    {
        public Task<ConsentDecision> EvaluateAsync(Guid userId, string clientId, string[] scopes, CancellationToken ct = default)
            => Task.FromResult(new ConsentDecision(false, true));
    }

    private sealed class NoProviderSelection : IProviderSelectionService
    {
        public Task<ProviderSelectionResult> EvaluateAsync(string clientId, string? idp, string? idpHint, string? lastUsedIdp, bool forceAccountSelection, CancellationToken ct = default, Guid? tenantId = null)
            => Task.FromResult(new ProviderSelectionResult(false, null));
    }

    private sealed class AlwaysAssigned : IUserClientAssignmentService
    {
        public Task<(bool assigned, string? error)> EnsureAssignedAsync(Guid userId, string clientId, string? idp, CancellationToken ct = default)
            => Task.FromResult((true, (string?)null));
    }

    private sealed class SimpleResponses : IAuthorizeResponseGenerator
    {
        public IResult CreateSuccessResponse(HttpContext http, AuthorizeValidationResult validation, string code, string? redirectUri) => Results.Ok();
        public IResult CreateErrorResponse(HttpContext http, AuthorizeValidationResult validation, string correlationId) => Results.BadRequest();
        public IResult CreateConsentRedirect(HttpContext http, AuthorizeValidationResult validation, string consentUrl) => Results.Redirect(consentUrl);
    }

    private sealed class NoSanitize : IAuthorizeRequestSanitizer
    {
        public IResult? SanitizeAddressBar(HttpContext http) => null;
    }

    private sealed class NoMetadata : IAuthorizationMetadataService
    {
        public Task PopulateMetadataAsync(HttpContext http, string code, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoQr : IQrLoginHandler
    {
        public Task<IResult> InitiateAsync(HttpContext http) => Task.FromResult(Results.Ok());
        public Task<IResult> InitiateAsync(HttpContext http, AuthorizeValidationResult validationResult, AuthorizeRequest request) => Task.FromResult(Results.Ok());
        public Task<IResult> GetStatusAsync(HttpContext http, string sessionToken) => Task.FromResult(Results.Ok());
        public Task<IResult> ConfirmAsync(HttpContext http) => Task.FromResult(Results.Ok());
        public Task<IResult> CancelAsync(HttpContext http) => Task.FromResult(Results.Ok());
        public Task<IResult> MobileLandingAsync(HttpContext http) => Task.FromResult(Results.Ok());
        public Task<IResult> ConfirmPageAsync(HttpContext http) => Task.FromResult(Results.Ok());
        public Task<IResult> CompleteAsync(HttpContext http, string sessionToken) => Task.FromResult(Results.Ok());
    }
}
