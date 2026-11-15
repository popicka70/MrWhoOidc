using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Services;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// OIDC implicit flow (OIDC Core §3): response_type id_token / id_token token validation, authorization
/// response encoding, and token issuance including at_hash.
/// </summary>
[TestClass]
public sealed class AuthorizeImplicitFlowTests
{
    private const string ClientId = "implicit-rp";
    private const string RedirectUri = "https://app/callback";
    private const string Issuer = "https://as.example.com";
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-00000000abcd");

    // ------------------------------ Request validation ------------------------------

    private static async Task<AuthorizeValidationResult> ValidateAsync(
        string responseType,
        string? nonce = "n1",
        string? responseMode = null,
        string[]? registeredResponseTypes = null,
        string tokenEndpointAuthMethod = "client_secret_basic")
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = ClientId,
            TokenEndpointAuthMethod = tokenEndpointAuthMethod,
            // Confidential marker so the code flow is not treated as a public client (PKCE not mandatory here).
            ClientSecretHash = "unit-test",
            RequirePkce = false,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { RedirectUri }),
            ResponseTypesJson = registeredResponseTypes is null ? null : JsonSerializer.Serialize(registeredResponseTypes),
        };
        db.Clients.Add(client);
        // R7 default-deny: the validator only accepts scopes assigned to the client.
        db.ClientScopes.AddRange(
            new ClientScope { ClientId = client.Id, ScopeName = "openid" },
            new ClientScope { ClientId = client.Id, ScopeName = "profile" });
        await db.SaveChangesAsync();

        var clients = new Mock<IClientStore>();
        clients.Setup(c => c.FindByClientIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(client);
        clients.Setup(c => c.GetActiveSecretsAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ClientSecret>());

        var validator = new AuthorizeRequestValidator(db, clients.Object, NullLogger<AuthorizeRequestValidator>.Instance);
        return await validator.ValidateAsync(new AuthorizeRequest(
            response_type: responseType,
            client_id: ClientId,
            redirect_uri: RedirectUri,
            scope: "openid profile",
            nonce: nonce,
            response_mode: responseMode));
    }

    [TestMethod]
    public async Task Validate_IdToken_WithNonce_IsValid_AndDefaultsToFragment()
    {
        var result = await ValidateAsync("id_token");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("id_token", result.ResponseType);
        // OAuth 2.0 Multiple Response Type Encoding Practices §2.1: fragment is the default for implicit.
        Assert.AreEqual("fragment", result.ResponseMode);
        Assert.IsTrue(result.IsImplicitFlow);
    }

    [TestMethod]
    public async Task Validate_TokenIdToken_IsNormalized_AndAccepted()
    {
        var result = await ValidateAsync("token id_token");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("id_token token", result.ResponseType);
        Assert.AreEqual("fragment", result.ResponseMode);
    }

    [TestMethod]
    public async Task Validate_IdTokenToken_CanonicalOrder_IsAccepted()
    {
        var result = await ValidateAsync("id_token token");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("id_token token", result.ResponseType);
    }

    [TestMethod]
    public async Task Validate_IdToken_WithoutNonce_IsRejected()
    {
        // OIDC Core §3.1.2.1: nonce is REQUIRED for implicit response types.
        var result = await ValidateAsync("id_token", nonce: null);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request", result.Error);
        StringAssert.Contains(result.ErrorDescription, "nonce");
    }

    [TestMethod]
    public async Task Validate_Implicit_WithQueryResponseMode_IsRejected()
    {
        // Tokens must never travel in the query string.
        var result = await ValidateAsync("id_token token", responseMode: "query");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request", result.Error);
        StringAssert.Contains(result.ErrorDescription, "response_mode=query");
    }

    [TestMethod]
    public async Task Validate_Implicit_WithFormPost_IsAllowed()
    {
        var result = await ValidateAsync("id_token", responseMode: "form_post");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("form_post", result.ResponseMode);
    }

    [TestMethod]
    public async Task Validate_HybridResponseType_IsRejected()
    {
        var result = await ValidateAsync("code id_token", nonce: "n1");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("unsupported_response_type", result.Error);
    }

    [TestMethod]
    public async Task Validate_PureOAuthImplicitToken_IsRejected()
    {
        var result = await ValidateAsync("token", nonce: "n1");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("unsupported_response_type", result.Error);
    }

    [TestMethod]
    public async Task Validate_Implicit_ResponseTypeNotRegisteredForClient_IsRejected()
    {
        var result = await ValidateAsync("id_token", registeredResponseTypes: new[] { "code" });

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("unauthorized_client", result.Error);
        StringAssert.Contains(result.ErrorDescription, "not registered");
    }

    [TestMethod]
    public async Task Validate_Implicit_RegisteredResponseType_IsAllowed()
    {
        var result = await ValidateAsync("id_token", registeredResponseTypes: new[] { "id_token", "code" });

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
    }

    [TestMethod]
    public async Task Validate_Implicit_DoesNotRequirePkce_ForPublicClient()
    {
        // A public client using the implicit flow cannot redeem a code, so no PKCE challenge is needed.
        var result = await ValidateAsync("id_token", tokenEndpointAuthMethod: "none");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
    }

    [TestMethod]
    public async Task Validate_Code_StillDefaultsToQueryMode()
    {
        var result = await ValidateAsync("code");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.AreEqual("query", result.ResponseMode);
        Assert.IsFalse(result.IsImplicitFlow);
    }

    // ------------------------------ Response encoding ------------------------------

    private static DefaultHttpContext CreateHttpContext()
    {
        var services = new ServiceCollection();

        services.AddLogging();

        services.AddSingleton<IMultiTenancyOptions>(new MultiTenancyStateProvider("default", initialEnabled: false));
        services.AddScoped<ITenantAccessor>(_ => MockTenantAccessor.CreateWithDefaultTenant());
        services.AddScoped<IIssuerBuilder, IssuerBuilder>();

        // Optional: make issuer deterministic.
        services.AddSingleton(new OidcOptions { Issuer = "https://test.example.com" });

        var sp = services.BuildServiceProvider();

        var http = new DefaultHttpContext
        {
            RequestServices = sp
        };

        http.Request.Scheme = "https";
        http.Request.Host = new HostString("test.example.com");
        http.Response.Body = new System.IO.MemoryStream();

        // Ensure GetIssuer() works.
        _ = http.GetIssuer();

        return http;
    }

    private static (AuthorizeResponseGenerator Generator, EphemeralDataProtectionProvider DataProtection) CreateGenerator()
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        return (new AuthorizeResponseGenerator(new StubJarm(), dataProtection), dataProtection);
    }

    private static async Task<string> UnprotectFinalUrlAsync(IResult result, HttpContext http, EphemeralDataProtectionProvider dataProtection)
    {
        var loc = await ExecuteRedirectLocationAsync(result, http);
        Assert.IsNotNull(loc);
        var baseUri = new Uri($"{http.Request.Scheme}://{http.Request.Host}");
        var outer = new Uri(baseUri, loc!);
        Assert.AreEqual("/Auth/Redirect", outer.AbsolutePath);

        var outerQuery = HttpUtility.ParseQueryString(outer.Query);
        var redirectUrl = outerQuery["redirectUrl"];
        Assert.IsFalse(string.IsNullOrWhiteSpace(redirectUrl));

        var protector = dataProtection.CreateProtector("MrWhoOidc.WebAuth.Pages.Auth.Redirect");
        return protector.Unprotect(redirectUrl!);
    }

    private static async Task<string?> ExecuteRedirectLocationAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        return context.Response.Headers.Location.FirstOrDefault();
    }

    [TestMethod]
    public async Task Generator_ImplicitIdToken_PutsTokensInFragmentOnly()
    {
        var http = CreateHttpContext();
        var (gen, dataProtection) = CreateGenerator();
        var validation = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: ClientId,
            RedirectUri: RedirectUri,
            ResponseType: "id_token",
            ResponseMode: "fragment",
            State: "state1",
            Nonce: "n1");

        var result = gen.CreateImplicitSuccessResponse(http, validation, idToken: "id.jwt.value", accessToken: null, expiresInSeconds: null);
        var finalUrl = await UnprotectFinalUrlAsync(result, http, dataProtection);

        var uri = new Uri(finalUrl);
        Assert.AreEqual("/callback", uri.AbsolutePath);
        Assert.IsTrue(string.IsNullOrEmpty(uri.Query) || uri.Query == "?", $"no query expected; got '{finalUrl}'");
        var fragment = HttpUtility.ParseQueryString(uri.Fragment.TrimStart('#'));
        Assert.AreEqual("id.jwt.value", fragment["id_token"]);
        Assert.AreEqual("state1", fragment["state"]);
        Assert.IsFalse(string.IsNullOrWhiteSpace(fragment["iss"]), "iss missing");
        Assert.IsFalse(string.IsNullOrWhiteSpace(fragment["session_state"]), "session_state missing");
        Assert.IsNull(fragment["access_token"], "no access token for response_type=id_token");
    }

    [TestMethod]
    public async Task Generator_ImplicitIdTokenToken_IncludesAccessTokenParams()
    {
        var http = CreateHttpContext();
        var (gen, dataProtection) = CreateGenerator();
        var validation = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: ClientId,
            RedirectUri: RedirectUri,
            ResponseType: "id_token token",
            ResponseMode: "fragment",
            State: "state1",
            Nonce: "n1");

        var result = gen.CreateImplicitSuccessResponse(http, validation, idToken: "id.jwt.value", accessToken: "at.jwt.value", expiresInSeconds: 3600);
        var finalUrl = await UnprotectFinalUrlAsync(result, http, dataProtection);

        var uri = new Uri(finalUrl);
        Assert.IsTrue(string.IsNullOrEmpty(uri.Query) || uri.Query == "?", $"no query expected; got '{finalUrl}'");
        var fragment = HttpUtility.ParseQueryString(uri.Fragment.TrimStart('#'));
        Assert.AreEqual("id.jwt.value", fragment["id_token"]);
        Assert.AreEqual("at.jwt.value", fragment["access_token"]);
        Assert.AreEqual("Bearer", fragment["token_type"]);
        Assert.AreEqual("3600", fragment["expires_in"]);
        Assert.AreEqual("state1", fragment["state"]);
    }

    [TestMethod]
    public async Task Generator_ImplicitFormPost_PostsTokens()
    {
        var http = CreateHttpContext();
        var (gen, _) = CreateGenerator();
        var validation = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: ClientId,
            RedirectUri: RedirectUri,
            ResponseType: "id_token",
            ResponseMode: "form_post",
            State: "state1");

        var result = gen.CreateImplicitSuccessResponse(http, validation, idToken: "id.jwt.value", accessToken: null, expiresInSeconds: null);
        await result.ExecuteAsync(http);

        Assert.AreEqual(200, http.Response.StatusCode);
        Assert.AreEqual("text/html; charset=utf-8", http.Response.ContentType);
        var body = await ReadBodyAsync(http);
        StringAssert.Contains(body, "id_token");
        StringAssert.Contains(body, "id.jwt.value");
        StringAssert.Contains(body, "state1");
        StringAssert.Contains(body, RedirectUri);
    }

    [TestMethod]
    public async Task Generator_ImplicitError_UsesFragment_WhenNoModeExplicit()
    {
        var http = CreateHttpContext();
        var (gen, _) = CreateGenerator();
        var validation = new AuthorizeValidationResult(
            IsValid: false,
            Error: "access_denied",
            ErrorDescription: "The user denied the authorization request",
            ClientId: ClientId,
            RedirectUri: RedirectUri,
            ResponseType: "id_token",
            ResponseMode: null,
            State: "state1");

        var result = gen.CreateErrorResponse(http, validation, "corr-1");
        var loc = await ExecuteRedirectLocationAsync(result, http);

        Assert.IsNotNull(loc);
        var uri = new Uri(loc!);
        Assert.IsTrue(string.IsNullOrEmpty(uri.Query) || uri.Query == "?", $"error must be in the fragment, not query; got '{loc}'");
        var fragment = HttpUtility.ParseQueryString(uri.Fragment.TrimStart('#'));
        Assert.AreEqual("access_denied", fragment["error"]);
        Assert.AreEqual("state1", fragment["state"]);
    }

    private static async Task<string> ReadBodyAsync(HttpContext http)
    {
        http.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new System.IO.StreamReader(http.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private sealed class StubJarm : MrWhoOidc.Auth.Services.IJarmService
    {
        public Task<string> CreateSuccessResponseAsync(string clientId, string issuer, string code, string responseMode, string? state)
            => Task.FromResult("jarm.jwt");
        public Task<string> CreateErrorResponseAsync(string clientId, string issuer, string error, string errorDescription, string? state)
            => Task.FromResult("jarm.jwt");
    }

    // ------------------------------ Token issuance ------------------------------

    private static (string privateJwkJson, string publicJwkJson) CreateSigningKey()
    {
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(true);
        var n = Base64UrlEncoder.Encode(parameters.Modulus);
        var e = Base64UrlEncoder.Encode(parameters.Exponent);
        var d = Base64UrlEncoder.Encode(parameters.D);
        var p = Base64UrlEncoder.Encode(parameters.P);
        var q = Base64UrlEncoder.Encode(parameters.Q);
        var dp = Base64UrlEncoder.Encode(parameters.DP);
        var dq = Base64UrlEncoder.Encode(parameters.DQ);
        var qi = Base64UrlEncoder.Encode(parameters.InverseQ);
        var kid = Guid.NewGuid().ToString("N");
        var priv = $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"use\":\"sig\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\",\"d\":\"{d}\",\"p\":\"{p}\",\"q\":\"{q}\",\"dp\":\"{dp}\",\"dq\":\"{dq}\",\"qi\":\"{qi}\"}}";
        var pub = $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"use\":\"sig\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\"}}";
        return (priv, pub);
    }

    private static ImplicitTokenService CreateIssuer(
        AuthDbContext db,
        string privateJwkJson,
        out JsonWebKey signingKey,
        Mock<IAccessTokenClaimBuilder>? claimBuilder = null)
    {
        signingKey = new JsonWebKey(privateJwkJson);
        var keyProvider = new Mock<ICachedKeyProvider>();
        keyProvider.Setup(k => k.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(signingKey);

        var pairwise = new Mock<IPairwiseSubjectService>();
        pairwise.Setup(p => p.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("pairwise-sub-1");

        var builder = claimBuilder ?? new Mock<IAccessTokenClaimBuilder>();
        builder
            .Setup(b => b.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>
            {
                new("sub", "pairwise-sub-1"),
                new("client_id", ClientId),
                new("scope", "openid profile"),
                new("jti", Guid.NewGuid().ToString("N"))
            });

        var settings = new Mock<ITenantSettingsService>();
        settings.Setup(s => s.GetTenantSettingsAsync(It.IsAny<Guid>())).ReturnsAsync((TenantSettings?)new TenantSettings());
        settings.Setup(s => s.GetPlatformDefaults()).Returns(new TenantSettings());

        var authOptions = Options.Create(new AuthOptions
        {
            ApiAudiences = new[] { "https://api.example.com" },
            EmitAmrInIdToken = false
        });

        return new ImplicitTokenService(
            db,
            new JwtService(keyProvider.Object),
            keyProvider.Object,
            pairwise.Object,
            builder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options.Create(new AuthOptions())),
            settings.Object,
            authOptions,
            NullLogger<ImplicitTokenService>.Instance);
    }

    private static async Task<(AuthDbContext db, MrWhoOidc.Auth.Persistence.Client client)> SeedAsync(string? idTokenSignedResponseAlg = null)
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = ClientId,
            TokenEndpointAuthMethod = "none",
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { RedirectUri }),
            IdTokenSignedResponseAlg = idTokenSignedResponseAlg
        };
        db.Clients.Add(client);
        db.Users.Add(new User
        {
            Id = UserId,
            Username = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            EmailVerified = true,
            Status = UserStatus.Active
        });
        await db.SaveChangesAsync();
        return (db, client);
    }

    private static AuthorizeValidationResult Validated(string responseType, string? claimsJson = null) => new(
        IsValid: true,
        ClientId: ClientId,
        RedirectUri: RedirectUri,
        ResponseType: responseType,
        Scopes: new[] { "openid", "profile" },
        Nonce: "n1",
        ResponseMode: "fragment",
        State: "s1",
        ClaimsJson: claimsJson);

    [TestMethod]
    public async Task Issue_IdTokenToken_IssuesBothTokens_WithValidAtHash()
    {
        var (db, client) = await SeedAsync();
        var (privateJwk, publicJwk) = CreateSigningKey();
        var signingKey = default(JsonWebKey);
        var issuer = CreateIssuer(db, privateJwk, out signingKey);
        var tenantId = Guid.NewGuid();

        var result = await issuer.IssueAsync(new ImplicitTokenRequest(
            client, UserId, Validated("id_token token"), Issuer, tenantId,
            AuthTime: DateTimeOffset.UtcNow.AddMinutes(-2),
            SessionId: "session-id-1"), CancellationToken.None);

        Assert.IsTrue(result.Ok, result.ErrorDescription ?? result.Error);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.IdToken));
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.IsNotNull(result.ExpiresInSeconds);
        Assert.IsTrue(result.ExpiresInSeconds!.Value > 0);

        // Persisted access token row (revocation/userinfo integrity)
        var stored = await db.Tokens.AsNoTracking().Where(t => t.ClientId == ClientId).ToListAsync();
        Assert.AreEqual(1, stored.Count);
        Assert.AreEqual("access", stored[0].Type);

        // Validate the ID token with the OP's public key
        var tvp = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudiences = new[] { ClientId },
            ValidateLifetime = true,
            IssuerSigningKeys = new[] { new JsonWebKey(publicJwk) },
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 }
        };
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(result.IdToken!, tvp, out var validated);

        Assert.AreEqual("pairwise-sub-1", principal.FindFirst("sub")?.Value);
        Assert.AreEqual("n1", principal.FindFirst("nonce")?.Value);
        Assert.IsNotNull(principal.FindFirst("auth_time"), "auth_time missing");
        Assert.AreEqual("session-id-1", principal.FindFirst("sid")?.Value);

        // OIDC Core §3.1.3.6: at_hash REQUIRED when an access token is issued alongside the ID token.
        var atHash = principal.FindFirst("at_hash")?.Value;
        Assert.IsFalse(string.IsNullOrWhiteSpace(atHash), "at_hash missing");
        var expectedHash = MrWhoOidc.Auth.Utils.CryptoHelper.ComputeLeftHalfHashBase64Url(result.AccessToken!, SecurityAlgorithms.RsaSha256);
        Assert.AreEqual(expectedHash, atHash, "at_hash must be the left half of the SHA-256 of the ASCII access token");

        // The access token is a JWT for the configured API audience
        var accessTokenJwt = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken!);
        Assert.AreEqual("https://api.example.com", accessTokenJwt.Audiences.First());
    }

    [TestMethod]
    public async Task Issue_IdTokenOnly_NoAccessToken_NoAtHash()
    {
        var (db, client) = await SeedAsync();
        var (privateJwk, _) = CreateSigningKey();
        var signingKey = default(JsonWebKey);
        var issuer = CreateIssuer(db, privateJwk, out signingKey);
        var tenantId = Guid.NewGuid();

        var result = await issuer.IssueAsync(new ImplicitTokenRequest(
            client, UserId, Validated("id_token"), Issuer, tenantId,
            AuthTime: DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken.None);

        Assert.IsTrue(result.Ok, result.ErrorDescription ?? result.Error);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.IdToken));
        Assert.IsNull(result.AccessToken);
        Assert.IsNull(result.ExpiresInSeconds);

        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(
            result.IdToken!,
            new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudiences = new[] { ClientId },
                ValidateLifetime = true,
                IssuerSigningKeys = new[] { signingKey },
                ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 }
            },
            out _);
        Assert.IsNull(principal.FindFirst("at_hash"), "at_hash must not be present without an access token");
        Assert.AreEqual("n1", principal.FindFirst("nonce")?.Value);
    }

    [TestMethod]
    public async Task Issue_UnavailableRegisteredSigningAlg_FailsClosed()
    {
        var (db, client) = await SeedAsync(idTokenSignedResponseAlg: "ES256");
        var (privateJwk, _) = CreateSigningKey();
        var signingKey = default(JsonWebKey);
        var issuer = CreateIssuer(db, privateJwk, out signingKey);
        var tenantId = Guid.NewGuid();

        var result = await issuer.IssueAsync(new ImplicitTokenRequest(
            client, UserId, Validated("id_token"), Issuer, tenantId), CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("invalid_request", result.Error);
    }

    [TestMethod]
    public async Task Issue_RegisteredNoneAlg_FailsClosed()
    {
        var (db, client) = await SeedAsync(idTokenSignedResponseAlg: "none");
        var (privateJwk, _) = CreateSigningKey();
        var signingKey = default(JsonWebKey);
        var issuer = CreateIssuer(db, privateJwk, out signingKey);
        var tenantId = Guid.NewGuid();

        var result = await issuer.IssueAsync(new ImplicitTokenRequest(
            client, UserId, Validated("id_token"), Issuer, tenantId), CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("invalid_request", result.Error);
    }

    [TestMethod]
    public async Task Issue_UserDeactivated_Fails()
    {
        var (db, client) = await SeedAsync();
        var user = await db.Users.SingleAsync(u => u.Id == UserId);
        user.Status = UserStatus.Deactivated;
        await db.SaveChangesAsync();

        var (privateJwk, _) = CreateSigningKey();
        var signingKey = default(JsonWebKey);
        var issuer = CreateIssuer(db, privateJwk, out signingKey);

        var result = await issuer.IssueAsync(new ImplicitTokenRequest(
            client, UserId, Validated("id_token"), Issuer, Guid.NewGuid()), CancellationToken.None);

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.ErrorDescription ?? "", "deactivated");
    }
}
