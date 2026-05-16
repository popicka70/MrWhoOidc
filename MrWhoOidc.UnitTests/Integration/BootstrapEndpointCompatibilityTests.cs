using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class BootstrapEndpointCompatibilityTests
{
    private const string BootstrapToken = "integration-bootstrap-token";

    private static WebApplicationFactory<Program> CreateEmptyDatabaseFactory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
            builder.UseSetting("Testing:UseInMemoryAuthDb", "true");
            builder.UseSetting("Testing:SkipAuthMigrations", "true");
            builder.UseSetting("Testing:AllowInMemoryFallback", "true");
            builder.UseSetting("Testing:InlineAuthCoreSafety", "true");
            builder.UseSetting("Testing:DisableServiceProviderValidation", "true");
            builder.UseSetting("Testing:ValidateAuthCore", "true");
            builder.UseSetting("Testing:DiagnoseAuthCore", "false");
            builder.UseSetting("Testing:DisableStaticAssets", "true");
            builder.UseSetting("Testing:DisableBackgroundServices", "true");
            builder.UseSetting("Testing:InMemoryDbName", $"BootstrapCompat_{Guid.NewGuid():N}");
            builder.UseSetting("MultiTenancy:Enabled", "false");
            builder.UseSetting("MultiTenancy:DefaultTenantSlug", "default");
            builder.UseSetting("ConnectionStrings:authdb", "Host=localhost;Database=fake;Username=fake;Password=fake");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddTestInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Testing:UseInMemoryAuthDb"] = "true",
                    ["Testing:SkipAuthMigrations"] = "true",
                    ["Testing:AllowInMemoryFallback"] = "true",
                    ["Testing:InlineAuthCoreSafety"] = "true",
                    ["Testing:DisableServiceProviderValidation"] = "true",
                    ["Testing:ValidateAuthCore"] = "true",
                    ["Testing:DiagnoseAuthCore"] = "false",
                    ["Testing:DisableStaticAssets"] = "true",
                    ["Testing:DisableBackgroundServices"] = "true",
                    ["MultiTenancy:Enabled"] = "false",
                    ["MultiTenancy:DefaultTenantSlug"] = "default",
                    ["ConnectionStrings:authdb"] = "Host=localhost;Database=fake;Username=fake;Password=fake",
                    ["Bootstrap:Token"] = BootstrapToken,
                    ["Oidc:PublicBaseUrl"] = "https://localhost"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IFileVersionProvider, NoopFileVersionProvider>();
            });
        });

    [TestMethod]
    public async Task LegacyApiBootstrapRoute_Bootstraps_Empty_Database()
    {
        using var factory = CreateEmptyDatabaseFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var payload = JsonSerializer.Serialize(new
        {
            tenantSlug = "default",
            tenantName = "Default Tenant",
            adminEmail = "admin@example.com",
            adminPassword = "ChangeMeNow123!",
            adminName = "Administrator"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bootstrap")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Bootstrap-Token", BootstrapToken);

        using var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, responseBody);

        using var document = JsonDocument.Parse(responseBody);
        Assert.AreEqual("default", document.RootElement.GetProperty("slug").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.AreEqual(1, await db.Tenants.CountAsync());
        Assert.IsTrue(await db.Tenants.AnyAsync(t => t.Slug == "default"));
    }
}