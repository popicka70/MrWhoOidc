using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.UnitTests.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Web;
using Microsoft.AspNetCore.DataProtection;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AuthorizeResponseGeneratorTests
{
    [TestMethod]
    public async Task AuthorizeResponseGenerator_QueryJwt_Places_Response_In_Query()
    {
        var http = CreateHttpContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"), dataProtection);

        var validation = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: "c1",
            ResponseMode: "query.jwt",
            State: "state1");

        var result = gen.CreateSuccessResponse(http, validation, code: "auth_code_123", redirectUri: "https://app/callback");
        var loc = await ExecuteRedirectLocationAsync(result, http);

        Assert.IsNotNull(loc);
        var uri = new Uri(loc!);
        Assert.AreEqual("https", uri.Scheme);
        Assert.AreEqual("app", uri.Host);
        Assert.AreEqual("/callback", uri.AbsolutePath);
        Assert.AreEqual(string.Empty, uri.Fragment);
        Assert.IsTrue(uri.Query.Contains("response=a.b.c", StringComparison.Ordinal), $"Expected response in query; got Location='{loc}'");
    }

    [TestMethod]
    public async Task AuthorizeResponseGenerator_FragmentJwt_Places_Response_In_Fragment()
    {
        var http = CreateHttpContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"), dataProtection);

        var validation = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: "c1",
            ResponseMode: "fragment.jwt",
            State: "state1");

        var result = gen.CreateSuccessResponse(http, validation, code: "auth_code_123", redirectUri: "https://app/callback");
        var loc = await ExecuteRedirectLocationAsync(result, http);

        Assert.IsNotNull(loc);
        var uri = new Uri(loc!);
        Assert.AreEqual("https", uri.Scheme);
        Assert.AreEqual("app", uri.Host);
        Assert.AreEqual("/callback", uri.AbsolutePath);
        Assert.IsTrue(string.IsNullOrEmpty(uri.Query) || uri.Query == "?", $"Expected no query params; got Location='{loc}'");
        Assert.IsTrue(uri.Fragment.Contains("response=a.b.c", StringComparison.Ordinal), $"Expected response in fragment; got Location='{loc}'");
    }

    [TestMethod]
    public void AuthorizeResponseGenerator_FormPostJwt_Returns_RazorPageResult()
    {
        var http = CreateHttpContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"), dataProtection);

        var validation = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: "c1",
            ResponseMode: "form_post.jwt",
            State: "state1");

        var result = gen.CreateSuccessResponse(http, validation, code: "auth_code_123", redirectUri: "https://app/callback");

        Assert.IsNotNull(result);
        Assert.AreEqual("RazorPageResult", result.GetType().Name);
    }

    [TestMethod]
    public void AuthorizeResponseGenerator_ErrorWithoutRedirectUri_Returns_RazorPageResult()
    {
        var http = CreateHttpContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"), dataProtection);

        var validation = new AuthorizeValidationResult(
            IsValid: false,
            Error: "invalid_request",
            ErrorDescription: "redirect_uri is not allowed for this client",
            ClientId: "c1");

        var result = gen.CreateErrorResponse(http, validation, "corr-123");

        Assert.IsNotNull(result);
        Assert.AreEqual("RazorPageResult", result.GetType().Name);
    }

    [TestMethod]
    public async Task AuthorizeResponseGenerator_NonJarm_Includes_SessionState_And_Sets_Opbs_Cookie()
    {
        var http = CreateHttpContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"), dataProtection);

        var validation = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: "c1",
            ResponseMode: "query",
            State: "state1",
            RedirectUri: "https://app/callback");

        var result = gen.CreateSuccessResponse(http, validation, code: "auth_code_123", redirectUri: "https://app/callback?code=auth_code_123&state=state1");
        var loc = await ExecuteRedirectLocationAsync(result, http);

        Assert.IsNotNull(loc);
        // Location may be relative (e.g., "/Auth/Redirect?..."), so use base URI from request context.
        var baseUri = new Uri($"{http.Request.Scheme}://{http.Request.Host}");
        var outer = new Uri(baseUri, loc!);
        Assert.AreEqual("/Auth/Redirect", outer.AbsolutePath);

        var outerQuery = HttpUtility.ParseQueryString(outer.Query);
        var redirectUrl = outerQuery["redirectUrl"];
        Assert.IsFalse(string.IsNullOrWhiteSpace(redirectUrl), "redirectUrl missing");

        var protector = dataProtection.CreateProtector("MrWhoOidc.WebAuth.Pages.Auth.Redirect");
        var unprotectedUrl = protector.Unprotect(redirectUrl!);

        var inner = new Uri(unprotectedUrl);
        var innerQuery = HttpUtility.ParseQueryString(inner.Query);
        Assert.IsFalse(string.IsNullOrWhiteSpace(innerQuery["session_state"]), "session_state missing");
        Assert.IsFalse(string.IsNullOrWhiteSpace(innerQuery["iss"]), "iss missing");
        Assert.AreEqual("state1", innerQuery["state"], "state must be echoed in the protected redirect URL");

        // Cookie should be set so check_session_iframe JS can read it.
        var setCookie = http.Response.Headers["Set-Cookie"].ToString();
        Assert.IsTrue(setCookie.Contains("__Host-mrwho-opbs=", StringComparison.Ordinal), $"Expected __Host-mrwho-opbs cookie; got '{setCookie}'");
    }

    [TestMethod]
    public async Task AuthorizeResponseGenerator_NonJarm_Omits_State_When_Not_Provided()
    {
        var http = CreateHttpContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"), dataProtection);

        var validation = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: "c1",
            ResponseMode: "query",
            State: null,
            RedirectUri: "https://app/callback");

        var result = gen.CreateSuccessResponse(http, validation, code: "auth_code_123", redirectUri: "https://app/callback");
        var loc = await ExecuteRedirectLocationAsync(result, http);

        Assert.IsNotNull(loc);
        var baseUri = new Uri($"{http.Request.Scheme}://{http.Request.Host}");
        var outer = new Uri(baseUri, loc!);
        var outerQuery = HttpUtility.ParseQueryString(outer.Query);
        var redirectUrl = outerQuery["redirectUrl"];
        Assert.IsFalse(string.IsNullOrWhiteSpace(redirectUrl), "redirectUrl missing");

        var protector = dataProtection.CreateProtector("MrWhoOidc.WebAuth.Pages.Auth.Redirect");
        var unprotectedUrl = protector.Unprotect(redirectUrl!);

        var inner = new Uri(unprotectedUrl);
        var innerQuery = HttpUtility.ParseQueryString(inner.Query);
        Assert.IsFalse(innerQuery.AllKeys.Contains("state", StringComparer.Ordinal), "state should not be present when validation.State is null");
    }

    [TestMethod]
    public async Task AuthorizeResponseGenerator_QueryError_Includes_Iss()
    {
        var http = CreateHttpContext();
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"), new EphemeralDataProtectionProvider());

        var validation = new AuthorizeValidationResult(
            IsValid: false,
            Error: "login_required",
            ErrorDescription: "no session",
            ClientId: "c1",
            RedirectUri: "https://app/callback",
            ResponseMode: "query",
            State: "state1");

        var loc = await ExecuteRedirectLocationAsync(gen.CreateErrorResponse(http, validation, "corr-1"), http);

        var query = HttpUtility.ParseQueryString(new Uri(loc!).Query);
        Assert.AreEqual("login_required", query["error"]);
        Assert.AreEqual("state1", query["state"]);
        Assert.AreEqual(http.GetIssuer(), query["iss"], "RFC 9207: error responses must carry iss");
    }

    [TestMethod]
    public async Task AuthorizeResponseGenerator_FormPostError_Includes_Iss()
    {
        var http = CreateHttpContext();
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"), new EphemeralDataProtectionProvider());

        var validation = new AuthorizeValidationResult(
            IsValid: false,
            Error: "access_denied",
            ErrorDescription: "denied",
            ClientId: "c1",
            RedirectUri: "https://app/callback",
            ResponseMode: "form_post",
            State: "state1");

        await gen.CreateErrorResponse(http, validation, "corr-1").ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var html = await new System.IO.StreamReader(http.Response.Body).ReadToEndAsync();

        Assert.IsTrue(html.Contains("name=\"iss\"", StringComparison.Ordinal), $"Expected iss form field; got '{html}'");
    }

    [TestMethod]
    public async Task AuthorizeResponseGenerator_FragmentError_Places_Parameters_In_Fragment()
    {
        var http = CreateHttpContext();
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"), new EphemeralDataProtectionProvider());

        var validation = new AuthorizeValidationResult(
            IsValid: false,
            Error: "login_required",
            ErrorDescription: "no session",
            ClientId: "c1",
            RedirectUri: "https://app/callback?keep=1",
            ResponseMode: "fragment",
            State: "state1");

        var loc = await ExecuteRedirectLocationAsync(gen.CreateErrorResponse(http, validation, "corr-1"), http);

        var uri = new Uri(loc!);
        var query = HttpUtility.ParseQueryString(uri.Query);
        var fragment = HttpUtility.ParseQueryString(uri.Fragment.TrimStart('#'));
        Assert.AreEqual("1", query["keep"]);
        Assert.IsNull(query["error"], $"error must not be in the query; got Location='{loc}'");
        Assert.AreEqual("login_required", fragment["error"]);
        Assert.AreEqual("state1", fragment["state"]);
        Assert.AreEqual(http.GetIssuer(), fragment["iss"]);
    }

    [TestMethod]
    public async Task AuthorizeResponseGenerator_FragmentSuccess_Places_Code_In_Fragment()
    {
        var http = CreateHttpContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"), dataProtection);

        var validation = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: "c1",
            ResponseMode: "fragment",
            State: "state1",
            RedirectUri: "https://app/callback");

        var result = gen.CreateSuccessResponse(http, validation, code: "auth_code_123", redirectUri: "https://app/callback?code=auth_code_123&state=state1");
        var loc = await ExecuteRedirectLocationAsync(result, http);

        var outer = new Uri(new Uri("https://test.example.com"), loc!);
        var redirectUrl = HttpUtility.ParseQueryString(outer.Query)["redirectUrl"];
        var inner = new Uri(dataProtection.CreateProtector("MrWhoOidc.WebAuth.Pages.Auth.Redirect").Unprotect(redirectUrl!));

        var query = HttpUtility.ParseQueryString(inner.Query);
        var fragment = HttpUtility.ParseQueryString(inner.Fragment.TrimStart('#'));
        Assert.IsNull(query["code"], $"code must not be in the query; got '{inner}'");
        Assert.AreEqual("auth_code_123", fragment["code"]);
        Assert.AreEqual("state1", fragment["state"]);
        Assert.AreEqual(http.GetIssuer(), fragment["iss"]);
    }

    [TestMethod]
    public async Task ConsentDeny_Redirects_To_Validated_RedirectUri_Not_ReturnUrl()
    {
        var http = CreateHttpContext();
        var session = new DictionarySession();
        http.Features.Set<Microsoft.AspNetCore.Http.Features.ISessionFeature>(new SessionFeature(session));
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"), new EphemeralDataProtectionProvider());

        var validation = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: "c1",
            RedirectUri: "https://app/callback",
            Scopes: new[] { "openid" },
            ResponseMode: "query",
            State: "state1");
        var consentLoc = await ExecuteRedirectLocationAsync(gen.CreateConsentRedirect(http, validation, "/consent"), http);
        var consentId = HttpUtility.ParseQueryString(new Uri(new Uri("https://test.example.com"), consentLoc!).Query)["ConsentId"];

        var model = new MrWhoOidc.WebAuth.Pages.ConsentModel(new Moq.Mock<MrWhoOidc.Auth.Services.IConsentService>().Object, gen)
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = http },
            ConsentId = consentId!,
            ClientId = "c1",
            ReturnUrl = "/authorize?client_id=c1&redirect_uri=https%3A%2F%2Fevil.example%2Fcb&state=attacker"
        };

        http.Response.Headers.Remove("Location");
        await model.OnPostDenyAsync();
        var loc = http.Response.Headers.Location.FirstOrDefault();

        Assert.IsNotNull(loc);
        var uri = new Uri(loc!);
        Assert.AreEqual("app", uri.Host, $"Deny must target the validated redirect_uri; got '{loc}'");
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.AreEqual("access_denied", query["error"]);
        Assert.AreEqual("state1", query["state"]);
        Assert.AreEqual(http.GetIssuer(), query["iss"]);
        Assert.IsFalse(session.Keys.Any(), "the consent challenge must be consumed");
    }

    private sealed class DictionarySession : ISession
    {
        private readonly Dictionary<string, byte[]> _values = new();
        public bool IsAvailable => true;
        public string Id { get; } = Guid.NewGuid().ToString();
        public IEnumerable<string> Keys => _values.Keys;
        public void Clear() => _values.Clear();
        public Task CommitAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _values.Remove(key);
        public void Set(string key, byte[] value) => _values[key] = value;
        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? value) => _values.TryGetValue(key, out value);
    }

    private sealed class SessionFeature(ISession session) : Microsoft.AspNetCore.Http.Features.ISessionFeature
    {
        public ISession Session { get; set; } = session;
    }

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

    private static async Task<string?> ExecuteRedirectLocationAsync(IResult result, DefaultHttpContext context)
    {
        // Only clear Location header, preserve Set-Cookie and other headers set before execute.
        context.Response.Headers.Remove("Location");
        await result.ExecuteAsync(context);
        return context.Response.Headers.Location.FirstOrDefault();
    }

    private sealed class StubJarmService : MrWhoOidc.Auth.Services.IJarmService
    {
        private readonly string _jwt;

        public StubJarmService(string jwt)
        {
            _jwt = jwt;
        }

        public Task<string> CreateSuccessResponseAsync(string clientId, string issuer, string code, string responseMode, string? state)
            => Task.FromResult(_jwt);

        public Task<string> CreateErrorResponseAsync(string clientId, string issuer, string error, string errorDescription, string? state)
            => Task.FromResult(_jwt);
    }
}
