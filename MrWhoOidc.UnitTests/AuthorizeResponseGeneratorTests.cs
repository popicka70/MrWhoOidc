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

        // Cookie should be set so check_session_iframe JS can read it.
        var setCookie = http.Response.Headers["Set-Cookie"].ToString();
        Assert.IsTrue(setCookie.Contains("mrwho.opbs=", StringComparison.Ordinal), $"Expected mrwho.opbs cookie; got '{setCookie}'");
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
