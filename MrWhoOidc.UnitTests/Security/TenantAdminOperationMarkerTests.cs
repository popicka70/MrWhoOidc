using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// R12: read-only support sessions are enforced from the endpoint's TenantAdminOperationKind. An endpoint without an
/// explicit marker falls back to the HTTP method, so a state-changing GET (or a sensitive POST classified as a
/// plain write) slips through. Every tenant-admin API endpoint must therefore say what it is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TenantAdminOperationMarkerTests
{
    /// <summary>
    /// Tenant-admin endpoints that have no marker yet. TODO: mark them in AdminApiEndpointMappingExtensions /
    /// ProviderAndBclEndpoints and empty this list; the test fails both for a new unmarked endpoint and for an entry
    /// here that has since been marked or removed.
    /// </summary>
    private static readonly HashSet<string> KnownUnmarked = new(StringComparer.Ordinal)
    {
        "GET /admin/api/bcl/alerts/snapshot",
        "GET /admin/api/bcl/outbox",
        "GET /admin/api/clients",
        "GET /admin/api/clients/{clientId:guid}/keys",
        "GET /admin/api/clients/{clientId:guid}/providers",
        "GET /admin/api/clients/{clientId:guid}/scopes",
        "GET /admin/api/clients/{clientId:guid}/secrets",
        "GET /admin/api/clients/{id:guid}",
        "GET /admin/api/clients/{id:guid}/export",
        "GET /admin/api/clients/{id:guid}/export/preview",
        "GET /admin/api/configuration-audit/",
        "GET /admin/api/configuration-audit/{id:guid}",
        "GET /admin/api/domain-claims",
        "GET /admin/api/invitations",
        "GET /admin/api/license",
        "GET /admin/api/license/history",
        "GET /admin/api/license/limits",
        "GET /admin/api/license/tiers",
        "GET /admin/api/license/usage",
        "GET /admin/api/platform/tenants/{slug}/export",
        "GET /admin/api/platform/tenants/{slug}/export/preview",
        "GET /admin/api/providers",
        "GET /admin/api/providers/{id:guid}",
        "GET /admin/api/providers/{id:guid}/export",
        "GET /admin/api/providers/{id:guid}/export/preview",
        "GET /admin/api/providers/{providerId:guid}/claim-mappings",
        "GET /admin/api/providers/{providerId:guid}/keys",
        "GET /admin/api/rate-limits/client/{clientId}",
        "GET /admin/api/rate-limits/events",
        "GET /admin/api/rate-limits/metrics",
        "GET /admin/api/rate-limits/overview",
        "GET /admin/api/realms",
        "GET /admin/api/realms/{id:guid}",
        "GET /admin/api/realms/{id:guid}/export",
        "GET /admin/api/realms/{id:guid}/export/preview",
        "GET /admin/api/registration-settings",
        "GET /admin/api/roles",
        "GET /admin/api/roles/{id:guid}",
        "GET /admin/api/scopes",
        "GET /admin/api/tenants/{tenantId:guid}/icon",
        "GET /admin/api/users",
        "GET /admin/api/users/{id:guid}",
        "GET /admin/api/users/{userId:guid}/clients",
        "GET /admin/api/users/{userId:guid}/roles",
        "GET /t/{slug}/admin/api/bcl/alerts/snapshot",
        "GET /t/{slug}/admin/api/bcl/outbox",
        "GET /t/{slug}/admin/api/clients",
        "GET /t/{slug}/admin/api/clients/{clientId:guid}/keys",
        "GET /t/{slug}/admin/api/clients/{clientId:guid}/providers",
        "GET /t/{slug}/admin/api/clients/{clientId:guid}/scopes",
        "GET /t/{slug}/admin/api/clients/{clientId:guid}/secrets",
        "GET /t/{slug}/admin/api/clients/{id:guid}",
        "GET /t/{slug}/admin/api/clients/{id:guid}/export",
        "GET /t/{slug}/admin/api/clients/{id:guid}/export/preview",
        "GET /t/{slug}/admin/api/configuration-audit/",
        "GET /t/{slug}/admin/api/configuration-audit/{id:guid}",
        "GET /t/{slug}/admin/api/domain-claims",
        "GET /t/{slug}/admin/api/invitations",
        "GET /t/{slug}/admin/api/license",
        "GET /t/{slug}/admin/api/license/history",
        "GET /t/{slug}/admin/api/license/limits",
        "GET /t/{slug}/admin/api/license/tiers",
        "GET /t/{slug}/admin/api/license/usage",
        "GET /t/{slug}/admin/api/providers",
        "GET /t/{slug}/admin/api/providers/{id:guid}",
        "GET /t/{slug}/admin/api/providers/{id:guid}/export",
        "GET /t/{slug}/admin/api/providers/{id:guid}/export/preview",
        "GET /t/{slug}/admin/api/providers/{providerId:guid}/claim-mappings",
        "GET /t/{slug}/admin/api/providers/{providerId:guid}/keys",
        "GET /t/{slug}/admin/api/rate-limits/client/{clientId}",
        "GET /t/{slug}/admin/api/rate-limits/events",
        "GET /t/{slug}/admin/api/rate-limits/metrics",
        "GET /t/{slug}/admin/api/rate-limits/overview",
        "GET /t/{slug}/admin/api/realms",
        "GET /t/{slug}/admin/api/realms/{id:guid}",
        "GET /t/{slug}/admin/api/realms/{id:guid}/export",
        "GET /t/{slug}/admin/api/realms/{id:guid}/export/preview",
        "GET /t/{slug}/admin/api/registration-settings",
        "GET /t/{slug}/admin/api/roles",
        "GET /t/{slug}/admin/api/roles/{id:guid}",
        "GET /t/{slug}/admin/api/scopes",
        "GET /t/{slug}/admin/api/tenants/{tenantId:guid}/icon",
        "GET /t/{slug}/admin/api/users",
        "GET /t/{slug}/admin/api/users/{id:guid}",
        "GET /t/{slug}/admin/api/users/{userId:guid}/clients",
        "GET /t/{slug}/admin/api/users/{userId:guid}/roles",
        "POST /admin/api/clients/import/",
        "POST /admin/api/clients/import/preview",
        "POST /admin/api/license",
        "POST /admin/api/license/validate",
        "POST /admin/api/platform/tenants/import/",
        "POST /admin/api/platform/tenants/import/preview",
        "POST /admin/api/providers/import/",
        "POST /admin/api/providers/import/preview",
        "POST /admin/api/realms/import/",
        "POST /admin/api/realms/import/preview",
        "POST /t/{slug}/admin/api/license",
        "POST /t/{slug}/admin/api/license/validate",
    };

    /// <summary>
    /// The authorization middleware adds endpoint metadata to the policy only when it is IAuthorizationRequirementData;
    /// a bare IAuthorizationRequirement in metadata is ignored, which left every WithOperation(...) marker inert.
    /// </summary>
    [TestMethod]
    public void OperationMarker_IsRequirementDataTheMiddlewareEnforces()
    {
        var marker = new TenantAdminOperationRequirement { Kind = TenantAdminOperationKind.Write };

        var data = (object)marker as IAuthorizationRequirementData;

        Assert.IsNotNull(data, "a bare IAuthorizationRequirement in endpoint metadata is ignored by the middleware");
        CollectionAssert.AreEqual(new object[] { marker }, data.GetRequirements().ToArray());
    }

    /// <summary>
    /// End to end: with the tenant-admin policy itself waved through, a marked endpoint is still decided by the
    /// marker (here: no real tenant-admin role, so 403), while an unmarked one is not.
    /// </summary>
    [TestMethod]
    public async Task OperationMarker_IsEvaluatedByTheAuthorizationMiddleware()
    {
        using var factory = ((WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory())
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, SignedInHandler>("Test", _ => { });
                    services.PostConfigure<AuthenticationOptions>(o =>
                    {
                        o.DefaultAuthenticateScheme = "Test";
                        o.DefaultChallengeScheme = "Test";
                    });
                    services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy("tenant-admin", p => p.RequireAssertion(_ => true)));
                });
            });
        var client = factory.CreateClient();

        var marked = await client.PostAsJsonAsync("/admin/api/invitations", new { email = "x@example.com", validDays = 1 });
        var unmarked = await client.GetAsync("/admin/api/invitations");

        Assert.AreEqual(HttpStatusCode.Forbidden, marked.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, unmarked.StatusCode);
    }

    private sealed class SignedInHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    [TestMethod, TestCategory("SafetySurface")]
    public void EveryTenantAdminApiEndpoint_DeclaresItsOperationKind()
    {
        using var factory = (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        var unmarked = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!pattern.StartsWith("/admin/api", StringComparison.OrdinalIgnoreCase)
                && !pattern.StartsWith("/t/{slug}/admin/api", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!endpoint.Metadata.OfType<TenantAdminOperationRequirement>().Any())
            {
                var methods = string.Join(',', endpoint.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods ?? []);
                unmarked.Add($"{methods} {pattern}");
            }
        }

        var unexpected = unmarked.Except(KnownUnmarked).ToList();
        var markedOutsideTenantAdmin = dataSource.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.OfType<TenantAdminOperationRequirement>().Any()
                        && !e.Metadata.OfType<IAuthorizeData>().Any(a => a.Policy == "tenant-admin"))
            .Select(e => e.RoutePattern.RawText)
            .ToList();
        Assert.IsEmpty(markedOutsideTenantAdmin, "The marker is enforced as a tenant-admin requirement; it only belongs on tenant-admin endpoints:\n" + string.Join('\n', markedOutsideTenantAdmin));
        var stale = KnownUnmarked.Except(unmarked).ToList();
        Assert.IsEmpty(unexpected, "Tenant-admin endpoints without WithOperation(...):\n" + string.Join('\n', unexpected));
        Assert.IsEmpty(stale, "Remove from KnownUnmarked (now marked or gone):\n" + string.Join('\n', stale));
    }
}
