using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class DiscoverySigningAlgTests
{
    [TestMethod]
    public async Task Discovery_SigningAlgs_Ignore_Encryption_And_Retired_Keys()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var tenantId = tenantAccessor.CurrentTenant!.TenantId;
        var now = DateTimeOffset.UtcNow;
        db.SigningKeys.AddRange(
            new SigningKey { TenantId = tenantId, Kid = "sig", Use = "sig", Alg = "PS256", CreatedAt = now.AddDays(-2) },
            new SigningKey { TenantId = tenantId, Kid = "retired", Use = "sig", Alg = "ES256", CreatedAt = now.AddDays(-1), RetiredAt = now },
            new SigningKey { TenantId = tenantId, Kid = "enc", Use = "enc", Alg = "RSA-OAEP", CreatedAt = now });
        await db.SaveChangesAsync();

        var platformSettings = new Mock<IPlatformSettingsService>();
        platformSettings.Setup(s => s.GetSettingsAsync()).ReturnsAsync(new PlatformSettings());
        var handler = new DiscoveryHandler(
            Microsoft.Extensions.Options.Options.Create(new OidcOptions { Issuer = "https://issuer.example.com" }),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            db,
            tenantAccessor,
            Mock.Of<ICliClientService>(),
            platformSettings.Object,
            new MultiTenancyStateProvider("default", initialEnabled: false));

        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("issuer.example.com");
        http.Response.Body = new MemoryStream();

        await (await handler.HandleAsync(http)).ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(http.Response.Body);

        var algs = doc.RootElement.GetProperty("id_token_signing_alg_values_supported").EnumerateArray().Select(e => e.GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "PS256" }, algs, "only the active, non-retired signing key's alg may be advertised");
    }
}
