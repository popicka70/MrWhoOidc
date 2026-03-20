using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;

namespace MrWhoOidc.UnitTests.Cli;

[TestClass]
public sealed class CliServerConnectionTests
{
    [TestMethod]
    public void ResolveServerUrlOrThrow_UsesExplicitServerWhenProvided()
    {
        var config = new CliConfig
        {
            CurrentProfile = "default",
            Profiles = new Dictionary<string, ProfileConfig>
            {
                ["default"] = new() { ServerUrl = "https://localhost:8443/t/default" }
            }
        };

        var resolved = CliServerConnection.ResolveServerUrlOrThrow(config, "https://localhost:8443/t/other");

        Assert.AreEqual("https://localhost:8443/t/other", resolved);
    }

    [TestMethod]
    public void ResolveServerUrlOrThrow_UsesCurrentProfileServerWhenExplicitServerMissing()
    {
        var config = new CliConfig
        {
            CurrentProfile = "default",
            Profiles = new Dictionary<string, ProfileConfig>
            {
                ["default"] = new() { ServerUrl = "https://localhost:8443/t/default" }
            }
        };

        var resolved = CliServerConnection.ResolveServerUrlOrThrow(config);

        Assert.AreEqual("https://localhost:8443/t/default", resolved);
    }

    [TestMethod]
    public void ResolveServerUrlOrThrow_UsesNamedProfileServer()
    {
        var config = new CliConfig
        {
            CurrentProfile = "default",
            Profiles = new Dictionary<string, ProfileConfig>
            {
                ["default"] = new() { ServerUrl = "https://localhost:8443/t/default" },
                ["admin"] = new() { ServerUrl = "https://localhost:8443/t/admin" }
            }
        };

        var resolved = CliServerConnection.ResolveServerUrlOrThrow(config, profileName: "admin");

        Assert.AreEqual("https://localhost:8443/t/admin", resolved);
    }

    [TestMethod]
    public void ResolveAuthenticatedConnectionOrThrow_UsesMatchingAuthenticatedProfileForExplicitServer()
    {
        var config = new CliConfig
        {
            CurrentProfile = "default",
            Profiles = new Dictionary<string, ProfileConfig>
            {
                ["default"] = new() { ServerUrl = "https://localhost:8443/t/default", AccessToken = "token-a" },
                ["admin"] = new() { ServerUrl = "https://localhost:8443", AccessToken = "token-b", IsPlatformAdmin = true }
            }
        };

        var resolved = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(config, "https://localhost:8443");

        Assert.AreEqual("admin", resolved.ProfileName);
        Assert.AreEqual("https://localhost:8443", resolved.ServerUrl);
        Assert.AreEqual("token-b", resolved.Profile.AccessToken);
    }

    [TestMethod]
    public void GetPlatformServerUrl_StripsTenantPath()
    {
        var platformServer = CliServerConnection.GetPlatformServerUrl("https://localhost:8443/t/acme");

        Assert.AreEqual("https://localhost:8443", platformServer);
    }

    [TestMethod]
    public async Task FetchDiscoveryAsync_AppliesFallbackEndpoints()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"issuer\":\"https://localhost:8443/t/default\"}", Encoding.UTF8, "application/json")
            }));

        var discovery = await CliServerConnection.FetchDiscoveryAsync(httpClient, "https://localhost:8443/t/default");

        Assert.AreEqual("https://localhost:8443/t/default", discovery.Issuer);
        Assert.AreEqual("https://localhost:8443/t/default/token", discovery.TokenEndpoint);
        Assert.AreEqual("https://localhost:8443/t/default/device/authorize", discovery.DeviceAuthorizationEndpoint);
    }

    [TestMethod]
    public async Task ReadJsonOrThrowAsync_NonJsonResponse_ThrowsHelpfulError()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Method Not Allowed", Encoding.UTF8, "text/plain")
        };

        InvalidOperationException? ex = null;

        try
        {
            await CliServerConnection.ReadJsonOrThrowAsync<object>(response, "token response");
        }
        catch (InvalidOperationException captured)
        {
            ex = captured;
        }

        Assert.IsNotNull(ex);
        StringAssert.Contains(ex.Message, "non-JSON token response");
        StringAssert.Contains(ex.Message, "Method Not Allowed");
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}