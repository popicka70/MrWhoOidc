using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth; // Program
using MrWhoOidc.UnitTests.Testing;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Phase 0 augmented safety net: ensures critical observable behaviors remain stable before deeper refactors.
/// These tests intentionally overlap with existing snapshot tests but add assertions for:
///  - Presence & exact names of rate limiting policies
///  - Admin authorization policy existence & handler registration
///  - Functional probe of core OIDC endpoints (status codes & minimal schema checks)
///  - Backchannel health endpoint contract keys
/// </summary>
[TestClass]
[DoNotParallelize]
public class Phase0AugmentedSafetyTests
{
    // Shared fixture eliminates per-test WebApplicationFactory creation overhead (~4s per test)
    private static SharedWebAppFixture _fixture = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new SharedWebAppFixture();

    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    [TestMethod, TestCategory("SafetySurface")]
    public void AdminPolicy_And_Handler_Are_Registered()
    {
        using var scope = _fixture.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var auth = sp.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = auth.GetPolicyAsync("admin").GetAwaiter().GetResult();
        Assert.IsNotNull(policy, "Admin policy 'admin' not found");
        Assert.IsTrue(policy!.Requirements.Any(r => r.GetType().Name.Contains("AdminRequirement", StringComparison.OrdinalIgnoreCase)),
            "AdminRequirement not present on 'admin' policy");
    }

    [TestMethod, TestCategory("SafetySurface")]
    public void RateLimitingPolicy_Names_Are_Stable()
    {
        var dataSource = _fixture.Services.GetRequiredService<EndpointDataSource>();
        var adminEndpoint = dataSource.Endpoints.FirstOrDefault(e => e.DisplayName != null && e.DisplayName.Contains("/admin/api", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(adminEndpoint, "Admin endpoint group not found");
        var hasRlAdmin = adminEndpoint!.Metadata.Any(md =>
        {
            var mt = md.GetType();
            var nameProp = mt.GetProperty("PolicyName") ?? mt.GetProperty("PolicyNames");
            if (nameProp == null) return false;
            var val = nameProp.GetValue(md);
            if (val is string s) return s.Equals("rl-admin", StringComparison.OrdinalIgnoreCase);
            if (val is IEnumerable<string> arr) return arr.Any(x => x.Equals("rl-admin", StringComparison.OrdinalIgnoreCase));
            return false;
        });
        Assert.IsTrue(hasRlAdmin, "rl-admin policy not applied to admin endpoint group");
    }

    [TestMethod, TestCategory("SafetySurface")]
    public async Task Core_Oidc_Endpoints_Functional_Probes()
    {
        using var client = _fixture.CreateClient();

        // Discovery can be served either from the root (single-tenant) or under /t/{slug} (multi-tenant).
        // Some environments may load a license that enables multi-tenancy, which intentionally blocks root discovery.
        var basePath = string.Empty;

        // Discovery
        var discovery = await client.GetAsync("/.well-known/openid-configuration");
        if (discovery.StatusCode == HttpStatusCode.NotFound)
        {
            var mt = _fixture.Services.GetRequiredService<IMultiTenancyStateProvider>();
            var slug = string.IsNullOrWhiteSpace(mt.DefaultTenantSlug) ? "default" : mt.DefaultTenantSlug;
            basePath = $"/t/{slug}";
            discovery = await client.GetAsync($"{basePath}/.well-known/openid-configuration");
        }
        Assert.AreEqual(HttpStatusCode.OK, discovery.StatusCode, "discovery status");
        var discoJson = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
        Assert.IsTrue(discoJson.RootElement.TryGetProperty("issuer", out _), "issuer missing");

        // JWKS
        var jwks = await client.GetAsync($"{basePath}/jwks");
        Assert.AreEqual(HttpStatusCode.OK, jwks.StatusCode, "jwks status");
        var jwksJson = JsonDocument.Parse(await jwks.Content.ReadAsStringAsync());
        Assert.IsTrue(jwksJson.RootElement.TryGetProperty("keys", out var jwksKeys) && jwksKeys.ValueKind == JsonValueKind.Array, "jwks keys array missing");
        foreach (var key in jwksKeys.EnumerateArray())
        {
            Assert.IsFalse(key.TryGetProperty("d", out _), "JWKS must not expose private exponent d");
            Assert.IsFalse(key.TryGetProperty("p", out _), "JWKS must not expose prime p");
            Assert.IsFalse(key.TryGetProperty("q", out _), "JWKS must not expose prime q");
            Assert.IsFalse(key.TryGetProperty("dp", out _), "JWKS must not expose CRT dp");
            Assert.IsFalse(key.TryGetProperty("dq", out _), "JWKS must not expose CRT dq");
            Assert.IsFalse(key.TryGetProperty("qi", out _), "JWKS must not expose CRT qi");
            Assert.IsFalse(key.TryGetProperty("oth", out _), "JWKS must not expose other prime info oth");
            Assert.IsFalse(key.TryGetProperty("k", out _), "JWKS must not expose symmetric key material k");
        }

        // Authorize (missing params) – expect 400 or redirect depending on handler logic; treat 200 as failure
        var authorize = await client.GetAsync($"{basePath}/authorize");
        Assert.IsTrue(authorize.StatusCode == HttpStatusCode.BadRequest || (int)authorize.StatusCode == 302, $"Unexpected authorize status {(int)authorize.StatusCode}");

        // Token (empty POST)
        var token = await client.PostAsync($"{basePath}/token", new FormUrlEncodedContent(new Dictionary<string, string>()));
        // Expecting 400 invalid_request
        Assert.AreEqual(HttpStatusCode.BadRequest, token.StatusCode, "token empty form should 400");

        // UserInfo (no auth) -> 401
        var userinfo = await client.GetAsync($"{basePath}/userinfo");
        Assert.AreEqual(HttpStatusCode.Unauthorized, userinfo.StatusCode, "userinfo without auth should 401");
    }

    [TestMethod, TestCategory("SafetySurface")]
    public async Task BackchannelHealth_Endpoint_Has_Expected_Shape()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.GetAsync("/health/backchannel");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode, "health/backchannel status");
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.IsTrue(root.TryGetProperty("enabled", out _), "enabled key missing");
        Assert.IsTrue(root.TryGetProperty("backlog", out _), "backlog key missing");
        Assert.IsTrue(root.TryGetProperty("openCircuits", out var oc) && oc.ValueKind == JsonValueKind.Array, "openCircuits missing or not array");
    }
}
