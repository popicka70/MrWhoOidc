using System.Net.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class CacheHeadersIntegrationTests
{
    [TestMethod]
    public async Task Revoke_Emits_NoStore_NoCache_Even_On_Error()
    {
        using var factory = (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Force an error path (wrong content type). Handler should still set cache headers.
        using var content = new StringContent("x");
        var resp = await client.PostAsync("/revoke", content);

        AssertHeader(resp, "Cache-Control", "no-store");
        AssertHeader(resp, "Pragma", "no-cache");
    }

    [TestMethod]
    public async Task Introspect_Emits_NoStore_NoCache_Even_On_Error()
    {
        using var factory = (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

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
