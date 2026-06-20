using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class RuntimeVersionEndpointTests
{
    private static Lazy<WebApplicationFactory<Program>> s_factory = null!;
    private static WebApplicationFactory<Program> Factory => s_factory.Value;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        s_factory = new Lazy<WebApplicationFactory<Program>>(() =>
            (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory());
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        if (s_factory?.IsValueCreated == true)
        {
            s_factory.Value.Dispose();
        }
    }

    [TestMethod]
    public async Task Version_Endpoint_Returns_Runtime_Metadata_And_Response_Headers()
    {
        using var client = Factory.CreateClient();
        using var response = await client.GetAsync("/version");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("X-MrWhoOidc-Version", out var headerValues));
        StringAssert.Contains(string.Join(',', response.Headers.CacheControl?.ToString() ?? string.Empty), "no-store");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        var assembly = typeof(Program).Assembly;
        var service = assembly.GetName().Name!;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var separatorIndex = informationalVersion.IndexOf('+');
        var version = separatorIndex > 0 ? informationalVersion[..separatorIndex] : informationalVersion;
        var commit = separatorIndex > 0 && separatorIndex < informationalVersion.Length - 1
            ? informationalVersion[(separatorIndex + 1)..]
            : null;

        Assert.AreEqual(service, root.GetProperty("service").GetString());
        Assert.AreEqual("Development", root.GetProperty("environment").GetString());
        Assert.AreEqual(version, root.GetProperty("version").GetString());
        Assert.AreEqual(informationalVersion, root.GetProperty("informationalVersion").GetString());
        CollectionAssert.Contains(headerValues!.ToArray(), informationalVersion);

        Assert.IsTrue(root.TryGetProperty("branch", out _));
        Assert.IsTrue(root.TryGetProperty("repoSlug", out _));
        Assert.IsTrue(root.TryGetProperty("serviceName", out _));

        if (!string.IsNullOrWhiteSpace(commit))
        {
            Assert.AreEqual(commit, root.GetProperty("commit").GetString());
            Assert.IsTrue(response.Headers.TryGetValues("X-MrWhoOidc-Commit", out var commitHeaderValues));
            CollectionAssert.Contains(commitHeaderValues!.ToArray(), commit);
        }
    }

    [TestMethod]
    public async Task Health_Endpoint_Includes_Runtime_Metadata()
    {
        using var client = Factory.CreateClient();
        using var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("X-MrWhoOidc-Version", out _));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var runtime = document.RootElement.GetProperty("runtime");

        Assert.AreEqual("MrWhoOidc.WebAuth", runtime.GetProperty("service").GetString());
        Assert.AreEqual("Development", runtime.GetProperty("environment").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(runtime.GetProperty("version").GetString()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(runtime.GetProperty("informationalVersion").GetString()));
        Assert.IsTrue(runtime.TryGetProperty("branch", out _));
        Assert.IsTrue(runtime.TryGetProperty("repoSlug", out _));
        Assert.IsTrue(runtime.TryGetProperty("serviceName", out _));
    }
}