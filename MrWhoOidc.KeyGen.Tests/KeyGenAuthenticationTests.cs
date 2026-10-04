using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.KeyGen.Persistence;
using MrWhoOidc.KeyGen.Security;

namespace MrWhoOidc.KeyGen.Tests;

/// <summary>
/// H7: KeyGen mints signed licenses and private JWKs, so every endpoint must require an
/// authenticated platform admin, startup must fail closed, and issuers must be recorded.
/// </summary>
[TestClass]
public sealed class KeyGenAuthenticationTests
{
    private const string AdminRole = "platform-admin";

    [TestMethod]
    [DataRow("/")]
    [DataRow("/KeyGeneration/Generate")]
    [DataRow("/KeyGeneration/List")]
    [DataRow("/LicenseGeneration/PlatformLicense")]
    [DataRow("/LicenseGeneration/TenantLicense")]
    [DataRow("/LicenseGeneration/Generate")]
    [DataRow("/LicenseGeneration/List")]
    public async Task Anonymous_page_request_is_redirected_to_the_identity_provider(string path)
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc();
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.GetAsync(path);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        StringAssert.StartsWith(location, KeyGenAppFactory.TestAuthorizationEndpoint);
        StringAssert.Contains(location, "response_type=code");
        StringAssert.Contains(location, "code_challenge_method=S256");
    }

    [TestMethod]
    public async Task Anonymous_post_to_license_generation_is_rejected_before_the_handler_runs()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc();
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.PostAsync("/LicenseGeneration/PlatformLicense", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Tier"] = "community",
            ["DeploymentMode"] = "single_tenant",
            ["Organization"] = "Attacker Inc"
        }));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        StringAssert.StartsWith(response.Headers.Location!.ToString(), KeyGenAppFactory.TestAuthorizationEndpoint);
        Assert.AreEqual(0, await CountLicensesAsync(factory));
    }

    [TestMethod]
    [DataRow("/api/keys/some-kid/public")]
    [DataRow("/api/keys/some-kid/private")]
    [DataRow("/api/licenses/00000000-0000-0000-0000-000000000000/download")]
    public async Task Anonymous_api_request_gets_401_not_a_redirect(string path)
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc();
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.GetAsync(path);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Health_endpoint_stays_anonymous()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc();
        using var client = factory.CreateNonRedirectingClient();

        using var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Signed_in_user_without_the_admin_role_is_forbidden()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc(useTestUserScheme: true);
        using var client = factory.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Add("X-Test-Sub", "user-1");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "tenant-admin");

        using var page = await client.GetAsync("/KeyGeneration/Generate");
        using var api = await client.GetAsync("/api/keys/some-kid/public");

        Assert.AreEqual(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, api.StatusCode);
    }

    [TestMethod]
    public async Task Signed_in_admin_can_open_generation_pages()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc(useTestUserScheme: true);
        using var client = factory.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Add("X-Test-Sub", "admin-1");
        client.DefaultRequestHeaders.Add("X-Test-Roles", AdminRole);

        using var response = await client.GetAsync("/KeyGeneration/Generate");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task License_records_the_signed_in_issuer_and_ignores_a_posted_CreatedBy()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc(useTestUserScheme: true);
        using var client = factory.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Add("X-Test-Sub", "admin-42");
        client.DefaultRequestHeaders.Add("X-Test-Name", "Jane Admin");
        client.DefaultRequestHeaders.Add("X-Test-Email", "jane@example.com");
        client.DefaultRequestHeaders.Add("X-Test-Roles", AdminRole);

        var token = await KeyGenAppFactory.GetAntiforgeryTokenAsync(client, "/LicenseGeneration/PlatformLicense");
        using var response = await client.PostAsync("/LicenseGeneration/PlatformLicense", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Tier"] = "community",
            ["DeploymentMode"] = "single_tenant",
            ["Organization"] = "Acme",
            ["NotBefore"] = DateTime.UtcNow.Date.ToString("yyyy-MM-dd"),
            ["ExpiresAt"] = DateTime.UtcNow.Date.AddYears(1).ToString("yyyy-MM-dd"),
            ["CreatedBy"] = "someone-else@example.com"
        }));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await using var db = OpenDb(factory);
        var license = await db.LicenseTokenMetadata.SingleAsync();
        Assert.AreEqual("Jane Admin <jane@example.com> (sub: admin-42)", license.GeneratedBy);
    }

    [TestMethod]
    public async Task Key_pair_records_the_signed_in_issuer()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc(useTestUserScheme: true);
        using var client = factory.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Add("X-Test-Sub", "admin-7");
        client.DefaultRequestHeaders.Add("X-Test-Email", "ops@example.com");
        client.DefaultRequestHeaders.Add("X-Test-Roles", AdminRole);

        var token = await KeyGenAppFactory.GetAntiforgeryTokenAsync(client, "/KeyGeneration/Generate");
        using var response = await client.PostAsync("/KeyGeneration/Generate", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Algorithm"] = "ES256",
            ["Curve"] = "P-256"
        }));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await using var db = OpenDb(factory);
        var key = await db.KeyPairMetadata.SingleAsync();
        Assert.AreEqual("ops@example.com (sub: admin-7)", key.CreatedBy);
    }

    [TestMethod]
    public async Task Secret_bearing_responses_are_not_cacheable()
    {
        using var factory = KeyGenAppFactory.ProductionWithOidc(useTestUserScheme: true);
        using var client = factory.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Add("X-Test-Sub", "admin-1");
        client.DefaultRequestHeaders.Add("X-Test-Roles", AdminRole);

        using var list = await client.GetAsync("/KeyGeneration/List");
        using var api = await client.GetAsync("/api/licenses/00000000-0000-0000-0000-000000000000/download");

        foreach (var response in new[] { list, api })
        {
            Assert.IsTrue(response.Headers.CacheControl?.NoStore == true, $"{response.RequestMessage!.RequestUri} lacks Cache-Control: no-store");
            CollectionAssert.Contains(response.Headers.Pragma.Select(p => p.Name).ToList(), "no-cache");
        }
    }

    [TestMethod]
    [DataRow("Production")]
    [DataRow("Staging")]
    public void Startup_fails_without_auth_configuration_outside_development(string environment)
    {
        using var factory = KeyGenAppFactory.Create(environment, new Dictionary<string, string?>());

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        StringAssert.Contains(ex.Message, "refusing to start");
    }

    [TestMethod]
    public void Development_opt_out_is_refused_outside_development()
    {
        using var factory = KeyGenAppFactory.Create("Production", new Dictionary<string, string?>
        {
            ["KeyGen:Auth:DisableInDevelopment"] = "true"
        });

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        StringAssert.Contains(ex.Message, "DisableInDevelopment");
    }

    [TestMethod]
    public void Development_without_auth_and_without_the_opt_out_also_fails()
    {
        using var factory = KeyGenAppFactory.Create("Development", new Dictionary<string, string?>());

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [TestMethod]
    public async Task Development_opt_out_signs_in_a_local_developer_and_records_it()
    {
        using var factory = KeyGenAppFactory.Create("Development", new Dictionary<string, string?>
        {
            ["KeyGen:Auth:DisableInDevelopment"] = "true"
        });
        using var client = factory.CreateNonRedirectingClient();

        var token = await KeyGenAppFactory.GetAntiforgeryTokenAsync(client, "/KeyGeneration/Generate");
        using var response = await client.PostAsync("/KeyGeneration/Generate", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Algorithm"] = "ES256",
            ["Curve"] = "P-256"
        }));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await using var db = OpenDb(factory);
        var key = await db.KeyPairMetadata.SingleAsync();
        Assert.AreEqual($"{DevelopmentAuthenticationHandler.DisplayName} (sub: {DevelopmentAuthenticationHandler.Subject})", key.CreatedBy);
    }

    private static KeyGenDbContext OpenDb(KeyGenAppFactory factory) =>
        new(new DbContextOptionsBuilder<KeyGenDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = factory.DatabasePath }.ToString())
            .Options);

    private static async Task<int> CountLicensesAsync(KeyGenAppFactory factory)
    {
        await using var db = OpenDb(factory);
        return await db.LicenseTokenMetadata.CountAsync();
    }
}
