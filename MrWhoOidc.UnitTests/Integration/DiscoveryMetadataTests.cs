using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests.Integration;

[TestClass]
public sealed class DiscoveryMetadataTests
{
    private static WebApplicationFactory<Program> CreateFactory()
        => (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();

    [TestMethod]
    public async Task Discovery_Advertises_Public_And_Pairwise_Subject_Types()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/.well-known/openid-configuration");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode, "discovery status");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.IsTrue(doc.RootElement.TryGetProperty("subject_types_supported", out var supported), "subject_types_supported missing");
        Assert.AreEqual(JsonValueKind.Array, supported.ValueKind, "subject_types_supported must be array");

        var values = supported.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        CollectionAssert.Contains(values, OidcConstants.SubjectTypes.Public);
        CollectionAssert.Contains(values, OidcConstants.SubjectTypes.Pairwise);
    }
}
