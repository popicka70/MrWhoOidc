using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
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
public class Phase0AugmentedSafetyTests
{
    private WebApplicationFactory<Program> CreateFactory() => (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();

    [TestMethod, TestCategory("SafetySurface")]
    public void AdminPolicy_And_Handler_Are_Registered()
    {
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
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
        using var factory = CreateFactory();
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
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
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        // Discovery
        var discovery = await client.GetAsync("/.well-known/openid-configuration");
        Assert.AreEqual(HttpStatusCode.OK, discovery.StatusCode, "discovery status");
        var discoJson = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
        Assert.IsTrue(discoJson.RootElement.TryGetProperty("issuer", out _), "issuer missing");

        // JWKS
        var jwks = await client.GetAsync("/jwks");
        Assert.AreEqual(HttpStatusCode.OK, jwks.StatusCode, "jwks status");

        // Authorize (missing params) – expect 400 or redirect depending on handler logic; treat 200 as failure
        var authorize = await client.GetAsync("/authorize");
        Assert.IsTrue(authorize.StatusCode == HttpStatusCode.BadRequest || (int)authorize.StatusCode == 302, $"Unexpected authorize status {(int)authorize.StatusCode}");

        // Token (empty POST)
        var token = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string,string>()));
        // Expecting 400 invalid_request
        Assert.AreEqual(HttpStatusCode.BadRequest, token.StatusCode, "token empty form should 400");

        // UserInfo (no auth) -> 401
        var userinfo = await client.GetAsync("/userinfo");
        Assert.AreEqual(HttpStatusCode.Unauthorized, userinfo.StatusCode, "userinfo without auth should 401");
    }

    [TestMethod, TestCategory("SafetySurface")]
    public async Task BackchannelHealth_Endpoint_Has_Expected_Shape()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/health/backchannel");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode, "health/backchannel status");
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.IsTrue(root.TryGetProperty("enabled", out _), "enabled key missing");
        Assert.IsTrue(root.TryGetProperty("backlog", out _), "backlog key missing");
        Assert.IsTrue(root.TryGetProperty("openCircuits", out var oc) && oc.ValueKind == JsonValueKind.Array, "openCircuits missing or not array");
    }
}
