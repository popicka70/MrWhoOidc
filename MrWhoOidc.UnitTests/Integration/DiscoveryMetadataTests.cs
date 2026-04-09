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
using Microsoft.Extensions.Configuration;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;
using System;

namespace MrWhoOidc.UnitTests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class DiscoveryMetadataTests
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
            s_factory.Value.Dispose();
    }

    // For tests that need isolated DB state (mutating tests)
    private static WebApplicationFactory<Program> CreateFactory()
        => (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();

    private static WebApplicationFactory<Program> CreateFactoryWithConfig(Dictionary<string, string?> config)
        => ((WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory()).WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddTestInMemoryCollection(config);
            });
        });

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
        var factory = Factory;
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
        var factory = Factory;
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
    public async Task Discovery_Advertises_mTLS_SelfSigned_Client_Auth_For_Token_And_Introspection()
    {
        var factory = Factory;
        using var doc = await GetDiscoveryAsync(factory);

        Assert.IsTrue(doc.RootElement.TryGetProperty("token_endpoint_auth_methods_supported", out var tokenAuth), "token_endpoint_auth_methods_supported missing");
        Assert.AreEqual(JsonValueKind.Array, tokenAuth.ValueKind, "token_endpoint_auth_methods_supported must be array");

        Assert.IsTrue(doc.RootElement.TryGetProperty("introspection_endpoint_auth_methods_supported", out var introspectionAuth), "introspection_endpoint_auth_methods_supported missing");
        Assert.AreEqual(JsonValueKind.Array, introspectionAuth.ValueKind, "introspection_endpoint_auth_methods_supported must be array");

        var tokenMethods = tokenAuth.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        var introspectionMethods = introspectionAuth.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        CollectionAssert.Contains(tokenMethods, "self_signed_tls_client_auth");
        CollectionAssert.Contains(introspectionMethods, "self_signed_tls_client_auth");
    }

    [TestMethod]
    public async Task Discovery_Advertises_tls_client_certificate_bound_access_tokens_Flag()
    {
        using var doc = await GetDiscoveryAsync(Factory);
        Assert.IsTrue(doc.RootElement.TryGetProperty("tls_client_certificate_bound_access_tokens", out var flag), "tls_client_certificate_bound_access_tokens missing");
        Assert.AreEqual(JsonValueKind.True, flag.ValueKind, "tls_client_certificate_bound_access_tokens should be true (server issues certificate-bound access tokens)");
    }

    [TestMethod]
    public async Task Discovery_Advertises_Claims_And_Resource_Indicator_Support()
    {
        using var doc = await GetDiscoveryAsync(Factory);

        Assert.IsTrue(doc.RootElement.TryGetProperty("claims_parameter_supported", out var claimsSupported), "claims_parameter_supported missing");
        Assert.AreEqual(JsonValueKind.True, claimsSupported.ValueKind, "claims_parameter_supported should be true");

        Assert.IsTrue(doc.RootElement.TryGetProperty("resource_indicators_supported", out var resourceSupported), "resource_indicators_supported missing");
        Assert.AreEqual(JsonValueKind.True, resourceSupported.ValueKind, "resource_indicators_supported should be true");
    }

    [TestMethod]
    public async Task Discovery_Advertises_Par_Metadata_Without_AdvancedSecurity_License()
    {
        using var doc = await GetDiscoveryAsync(Factory);

        Assert.IsTrue(doc.RootElement.TryGetProperty("pushed_authorization_request_endpoint", out var parEndpoint), "pushed_authorization_request_endpoint missing");
        Assert.AreEqual(JsonValueKind.String, parEndpoint.ValueKind, "pushed_authorization_request_endpoint must be string");
        Assert.IsTrue(parEndpoint.GetString()!.EndsWith("/par", StringComparison.Ordinal), $"Unexpected pushed_authorization_request_endpoint='{parEndpoint.GetString()}'");

        Assert.IsTrue(doc.RootElement.TryGetProperty("require_pushed_authorization_requests", out var requirePar), "require_pushed_authorization_requests missing");
        Assert.IsTrue(requirePar.ValueKind is JsonValueKind.True or JsonValueKind.False, "require_pushed_authorization_requests must be boolean");
    }

    [TestMethod]
    public async Task Discovery_Advertises_CheckSessionIFrame()
    {
        var factory = Factory;
        using var doc = await GetDiscoveryAsync(factory);

        Assert.IsTrue(doc.RootElement.TryGetProperty("check_session_iframe", out var iframe), "check_session_iframe missing");
        Assert.AreEqual(JsonValueKind.String, iframe.ValueKind, "check_session_iframe must be string");

        var val = iframe.GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(val), "check_session_iframe empty");
        Assert.IsTrue(val!.EndsWith("/connect/checksession", StringComparison.Ordinal), $"Unexpected check_session_iframe='{val}'");
    }

    [TestMethod]
    public async Task CheckSessionIFrame_Endpoint_Is_Embeddable()
    {
        var factory = Factory;

        var mt = factory.Services.GetRequiredService<IMultiTenancyStateProvider>();
        mt.UpdateState(false);

        var client = factory.CreateClient();
        var resp = await client.GetAsync("/connect/checksession");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);

        var ct = resp.Content.Headers.ContentType?.MediaType;
        Assert.AreEqual("text/html", ct);

        // Must not be DENY / none for framing.
        Assert.IsFalse(resp.Headers.TryGetValues("X-Frame-Options", out var xfo) && string.Join(" ", xfo).Contains("DENY", StringComparison.OrdinalIgnoreCase), "X-Frame-Options DENY blocks iframe usage");
        Assert.IsFalse(resp.Headers.TryGetValues("Content-Security-Policy", out var csp) && string.Join(" ", csp).Contains("frame-ancestors 'none'", StringComparison.OrdinalIgnoreCase), "CSP frame-ancestors 'none' blocks iframe usage");
    }

    [TestMethod]
    public async Task Discovery_Emits_mtls_endpoint_aliases_When_Configured()
    {
        using var factory = CreateFactoryWithConfig(new()
        {
            ["Auth:MtlsEndpointAliasesBaseUrl"] = "https://mtls.example.com"
        });

        using var doc = await GetDiscoveryAsync(factory);

        Assert.IsTrue(doc.RootElement.TryGetProperty("mtls_endpoint_aliases", out var aliases), "mtls_endpoint_aliases missing");
        Assert.AreEqual(JsonValueKind.Object, aliases.ValueKind, "mtls_endpoint_aliases must be object");

        Assert.IsTrue(aliases.TryGetProperty("token_endpoint", out var token), "token_endpoint alias missing");
        Assert.IsTrue(aliases.TryGetProperty("introspection_endpoint", out var introspect), "introspection_endpoint alias missing");
        Assert.IsTrue(aliases.TryGetProperty("revocation_endpoint", out var revoke), "revocation_endpoint alias missing");

        Assert.AreEqual("https://mtls.example.com/token", token.GetString());
        Assert.AreEqual("https://mtls.example.com/introspect", introspect.GetString());
        Assert.AreEqual("https://mtls.example.com/revoke", revoke.GetString());
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

    [TestMethod]
    public async Task Discovery_DoesNotAdvertise_Jarm_Encryption_When_NoClient_OptsIn()
    {
        using var factory = CreateFactory();

        // Arrange: ensure this tenant has no clients opting into JARM encryption.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var tenantId = db.Tenants.Select(t => t.Id).First();

            var toRemove = db.Clients.Where(c => c.TenantId == tenantId).ToList();
            if (toRemove.Count > 0)
            {
                db.Clients.RemoveRange(toRemove);
                db.SaveChanges();
            }
        }

        // Act
        using var doc = await GetDiscoveryAsync(factory);

        // Assert
        Assert.IsFalse(
            doc.RootElement.TryGetProperty("authorization_response_encryption_alg_values_supported", out _),
            "authorization_response_encryption_alg_values_supported should be omitted unless at least one client opts in");
        Assert.IsFalse(
            doc.RootElement.TryGetProperty("authorization_response_encryption_enc_values_supported", out _),
            "authorization_response_encryption_enc_values_supported should be omitted unless at least one client opts in");
    }

    [TestMethod]
    public async Task Discovery_Advertises_Jarm_Encryption_When_AnyClient_OptsIn()
    {
        using var factory = CreateFactory();

        // Arrange: seed a client that opts into authorization response encryption.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var tenantId = db.Tenants.Select(t => t.Id).First();

            // Ensure a realm exists for this tenant.
            var realm = db.Realms.FirstOrDefault(r => r.TenantId == tenantId);
            if (realm is null)
            {
                realm = new global::MrWhoOidc.Auth.Persistence.Realm { Name = "default", TenantId = tenantId };
                db.Realms.Add(realm);
                db.SaveChanges();
            }

            db.Clients.Add(new global::MrWhoOidc.Auth.Persistence.Client
            {
                TenantId = tenantId,
                RealmId = realm.Id,
                ClientId = "jarm-enc-client",
                AuthorizationEncryptedResponseAlg = "RSA-OAEP",
                AuthorizationEncryptedResponseEnc = "A256CBC-HS512"
            });

            db.SaveChanges();
        }

        // Act
        using var doc = await GetDiscoveryAsync(factory);

        // Assert
        Assert.IsTrue(
            doc.RootElement.TryGetProperty("authorization_response_encryption_alg_values_supported", out var algs),
            "authorization_response_encryption_alg_values_supported missing");
        Assert.IsTrue(
            doc.RootElement.TryGetProperty("authorization_response_encryption_enc_values_supported", out var encs),
            "authorization_response_encryption_enc_values_supported missing");

        var algValues = algs.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        var encValues = encs.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

        CollectionAssert.Contains(algValues!, "RSA-OAEP");
        CollectionAssert.Contains(encValues!, "A256CBC-HS512");
    }
}
