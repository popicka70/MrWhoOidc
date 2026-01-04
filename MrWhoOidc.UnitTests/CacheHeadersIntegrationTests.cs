using System.Net.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class CacheHeadersIntegrationTests
{
    // Shared fixture eliminates per-test WebApplicationFactory creation overhead
    private static SharedWebAppFixture _fixture = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new SharedWebAppFixture();

    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    [TestMethod]
    public async Task Revoke_Emits_NoStore_NoCache_Even_On_Error()
    {
        using var client = _fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Force an error path (wrong content type). Handler should still set cache headers.
        using var content = new StringContent("x");
        var resp = await client.PostAsync("/revoke", content);

        AssertHeader(resp, "Cache-Control", "no-store");
        AssertHeader(resp, "Pragma", "no-cache");
    }

    [TestMethod]
    public async Task Introspect_Emits_NoStore_NoCache_Even_On_Error()
    {
        using var client = _fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Force an error path (wrong content type / parse error). Handler should still set cache headers.
        using var content = new StringContent("x");
        var resp = await client.PostAsync("/introspect", content);

        AssertHeader(resp, "Cache-Control", "no-store");
        AssertHeader(resp, "Pragma", "no-cache");
    }

    private static void AssertHeader(HttpResponseMessage response, string name, string expectedValue)
    {
        Assert.IsTrue(response.Headers.TryGetValues(name, out var values), $"Missing {name} header");
        var joined = string.Join(",", values!);
        StringAssert.Contains(joined, expectedValue, $"Expected {name} to contain '{expectedValue}', got '{joined}'");
    }
}
