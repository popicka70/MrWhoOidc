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

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AuthorizeResponseGeneratorTests
{
    [TestMethod]
    public async Task AuthorizeResponseGenerator_QueryJwt_Places_Response_In_Query()
    {
        var http = CreateHttpContext();
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"));

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
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"));

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
        var gen = new AuthorizeResponseGenerator(new StubJarmService("a.b.c"));

        var validation = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: "c1",
            ResponseMode: "form_post.jwt",
            State: "state1");

        var result = gen.CreateSuccessResponse(http, validation, code: "auth_code_123", redirectUri: "https://app/callback");

        Assert.IsNotNull(result);
        Assert.AreEqual("RazorPageResult", result.GetType().Name);
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
        context.Response.Headers.Clear();
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
