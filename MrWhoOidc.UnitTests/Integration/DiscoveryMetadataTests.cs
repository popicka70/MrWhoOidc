using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.UnitTests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class DiscoveryMetadataTests
{
    private static WebApplicationFactory<Program> CreateFactory()
        => (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();

    private static async Task<JsonDocument> GetDiscoveryAsync(WebApplicationFactory<Program> factory)
    {
        // Ensure tests that hit root discovery are running in single-tenant mode.
        // Some environments may load a license that enables multi-tenancy, which would intentionally
        // block root discovery.
        var mt = factory.Services.GetRequiredService<IMultiTenancyStateProvider>();
        mt.UpdateState(false);

        var client = factory.CreateClient();
        var resp = await client.GetAsync("/.well-known/openid-configuration");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode, "discovery status");
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Discovery_Advertises_Public_And_Pairwise_Subject_Types()
    {
        using var factory = CreateFactory();
        using var doc = await GetDiscoveryAsync(factory);
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

    [TestMethod]
    public async Task Discovery_Advertises_Claims_Supported()
    {
        using var factory = CreateFactory();
        using var doc = await GetDiscoveryAsync(factory);

        Assert.IsTrue(doc.RootElement.TryGetProperty("claims_supported", out var supported), "claims_supported missing");
        Assert.AreEqual(JsonValueKind.Array, supported.ValueKind, "claims_supported must be array");

        Assert.IsTrue(doc.RootElement.TryGetProperty("scopes_supported", out var scopesSupported), "scopes_supported missing");
        Assert.AreEqual(JsonValueKind.Array, scopesSupported.ValueKind, "scopes_supported must be array");

        var values = supported.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        var scopes = scopesSupported.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        // Always supported.
        CollectionAssert.Contains(values, OidcConstants.Claims.Subject);

        // Scope-dependent claims: validate only when the scope is advertised.
        if (scopes.Contains(OidcConstants.Scopes.Profile))
        {
            CollectionAssert.Contains(values, OidcConstants.Claims.Name);
        }

        if (scopes.Contains(OidcConstants.Scopes.Email))
        {
            CollectionAssert.Contains(values, OidcConstants.Claims.Email);
            CollectionAssert.Contains(values, OidcConstants.Claims.EmailVerified);
            CollectionAssert.Contains(values, "emails");
        }

        if (scopes.Contains(OidcConstants.Scopes.Roles))
        {
            CollectionAssert.Contains(values, OidcConstants.Claims.Roles);
            CollectionAssert.Contains(values, OidcConstants.Claims.Realm);
        }

        if (scopes.Contains(OidcConstants.Scopes.Tenants))
        {
            CollectionAssert.Contains(values, OidcConstants.Scopes.Tenants);
        }
    }

    [TestMethod]
    public async Task Discovery_TenantPrefixed_Only_Advertises_Tenant_And_Global_Scopes()
    {
        using var factory = CreateFactory();

        // Arrange: ensure we have a second tenant and two tenant-specific scopes.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            var existingTenant = db.Tenants.FirstOrDefault();
            Assert.IsNotNull(existingTenant, "expected at least one seeded tenant");

            var other = db.Tenants.FirstOrDefault(t => t.Slug == "other");
            if (other is null)
            {
                other = new Tenant
                {
                    Slug = "other",
                    Name = "Other",
                    IssuerUri = "https://mrwho.local:8443/t/other",
                    Status = TenantStatus.Active
                };
                db.Tenants.Add(other);
                db.SaveChanges();
            }

            // Ensure a tenant-scoped scope for each tenant.
            var scopeA = "tenantA.custom";
            var scopeB = "tenantB.custom";

            if (!db.Scopes.Any(s => s.Name == scopeA))
            {
                db.Scopes.Add(new Scope { Name = scopeA, TenantId = existingTenant!.Id, IsExposed = true, IsGlobal = false });
            }
            if (!db.Scopes.Any(s => s.Name == scopeB))
            {
                db.Scopes.Add(new Scope { Name = scopeB, TenantId = other!.Id, IsExposed = true, IsGlobal = false });
            }

            db.SaveChanges();
        }

        // Act: fetch discovery for the first tenant.
        var client = factory.CreateClient();

        // Pick a seeded tenant slug dynamically to avoid hard-coding "default".
        string tenantSlug;
        using (var scope2 = factory.Services.CreateScope())
        {
            var db2 = scope2.ServiceProvider.GetRequiredService<AuthDbContext>();
            tenantSlug = db2.Tenants.Select(t => t.Slug).First();
        }

        var resp = await client.GetAsync($"/t/{tenantSlug}/.well-known/openid-configuration");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode, "tenant discovery status");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.IsTrue(doc.RootElement.TryGetProperty("scopes_supported", out var scopesSupported), "scopes_supported missing");
        Assert.AreEqual(JsonValueKind.Array, scopesSupported.ValueKind, "scopes_supported must be array");

        var scopes = scopesSupported.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        CollectionAssert.Contains(scopes, "tenantA.custom");
        CollectionAssert.DoesNotContain(scopes, "tenantB.custom");
    }

    [TestMethod]
    public async Task Discovery_Root_Is_Blocked_In_MultiTenant_Mode()
    {
        using var factory = CreateFactory();

        // Force multi-tenant mode ON for this test.
        var mt = factory.Services.GetRequiredService<IMultiTenancyStateProvider>();
        mt.UpdateState(true);

        var client = factory.CreateClient();
        var resp = await client.GetAsync("/.well-known/openid-configuration");
        Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode, "root discovery should be blocked in multi-tenant mode");
    }
}
