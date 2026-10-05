using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.WebAuth.Infrastructure.Pipeline;
using MrWhoOidc.WebAuth.Middleware;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class HostAllowListWildcardTests
{
    [TestMethod]
    public async Task Wildcard_WithoutOptIn_DoesNotDisableHostValidation()
    {
        var status = await InvokeAsync("evil.example.net", new()
        {
            ["ForwardedHeaders:EnforceHostAllowList"] = "true",
            ["ForwardedHeaders:AllowedHosts:0"] = "*",
            ["Oidc:PublicBaseUrl"] = "https://auth.example.com"
        });

        Assert.AreEqual(StatusCodes.Status400BadRequest, status);
    }

    [TestMethod]
    public async Task Wildcard_WithoutOptIn_StillAllowsCanonicalHost()
    {
        var status = await InvokeAsync("auth.example.com", new()
        {
            ["ForwardedHeaders:EnforceHostAllowList"] = "true",
            ["ForwardedHeaders:AllowedHosts:0"] = "*",
            ["Oidc:PublicBaseUrl"] = "https://auth.example.com"
        });

        Assert.AreEqual(StatusCodes.Status200OK, status);
    }

    [TestMethod]
    public async Task Wildcard_WithExplicitOptIn_AllowsAnyHost()
    {
        var status = await InvokeAsync("evil.example.net", new()
        {
            ["ForwardedHeaders:EnforceHostAllowList"] = "true",
            ["ForwardedHeaders:AllowedHosts:0"] = "*",
            ["ForwardedHeaders:AllowAnyHost"] = "true",
            ["Oidc:PublicBaseUrl"] = "https://auth.example.com"
        });

        Assert.AreEqual(StatusCodes.Status200OK, status);
    }

    [TestMethod]
    public void ForwardedHeadersConfigurator_DropsWildcard_WithoutOptIn()
    {
        var configuration = Build(new()
        {
            ["ForwardedHeaders:AllowedHosts:0"] = "*",
            ["Oidc:Issuer"] = "https://auth.example.com"
        });

        Assert.IsTrue(ForwardedHeadersConfigurator.TryBuild(configuration, Env(), NullLogger.Instance, out ForwardedHeadersOptions options));

        CollectionAssert.DoesNotContain(options.AllowedHosts.ToList(), "*");
        CollectionAssert.Contains(options.AllowedHosts.ToList(), "auth.example.com");
    }

    [TestMethod]
    public void ForwardedHeadersConfigurator_KeepsWildcard_WithOptIn()
    {
        var configuration = Build(new()
        {
            ["ForwardedHeaders:AllowedHosts:0"] = "*",
            ["ForwardedHeaders:AllowAnyHost"] = "true"
        });

        Assert.IsTrue(ForwardedHeadersConfigurator.TryBuild(configuration, Env(), NullLogger.Instance, out ForwardedHeadersOptions options));

        CollectionAssert.Contains(options.AllowedHosts.ToList(), "*");
    }

    private static async Task<int> InvokeAsync(string host, Dictionary<string, string?> settings)
    {
        var middleware = new HostAllowListMiddleware(
            ctx => { ctx.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; },
            Build(settings),
            Env(),
            NullLogger<HostAllowListMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return context.Response.StatusCode;
    }

    private static IConfiguration Build(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static IHostEnvironment Env() => new HostingEnvironment { EnvironmentName = Environments.Production };
}
