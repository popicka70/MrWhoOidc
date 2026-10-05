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
    /// marker: without a real tenant-admin role it is 403, and once something satisfies the marker requirement it
    /// is 200.
    /// </summary>
    [TestMethod]
    [DataRow(false, HttpStatusCode.Forbidden)]
    [DataRow(true, HttpStatusCode.OK)]
    public async Task OperationMarker_IsEvaluatedByTheAuthorizationMiddleware(bool satisfyMarker, HttpStatusCode expected)
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
                    if (satisfyMarker)
                    {
                        services.AddSingleton<IAuthorizationHandler, SatisfyMarkerHandler>();
                    }
                });
            });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/api/invitations");

        Assert.AreEqual(expected, response.StatusCode);
    }

    private sealed class SatisfyMarkerHandler : AuthorizationHandler<TenantAdminOperationRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantAdminOperationRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Credential-disclosing reads must be SecuritySensitiveRead (denied to read-only support sessions) and imports,
    /// which create clients, secrets, provider credentials and roles in bulk, SecuritySensitiveWrite.
    /// </summary>
    [TestMethod, TestCategory("SafetySurface")]
    [DataRow("GET /admin/api/clients/{clientId:guid}/secrets", TenantAdminOperationKind.SecuritySensitiveRead)]
    [DataRow("GET /t/{slug}/admin/api/clients/{clientId:guid}/secrets", TenantAdminOperationKind.SecuritySensitiveRead)]
    [DataRow("GET /admin/api/providers/{providerId:guid}/keys", TenantAdminOperationKind.SecuritySensitiveRead)]
    [DataRow("GET /admin/api/clients/{id:guid}/export", TenantAdminOperationKind.SecuritySensitiveRead)]
    [DataRow("GET /admin/api/realms/{id:guid}/export", TenantAdminOperationKind.SecuritySensitiveRead)]
    [DataRow("GET /admin/api/providers/{id:guid}/export", TenantAdminOperationKind.SecuritySensitiveRead)]
    [DataRow("GET /t/{slug}/admin/api/providers/{id:guid}/export", TenantAdminOperationKind.SecuritySensitiveRead)]
    [DataRow("GET /admin/api/clients/{id:guid}/export/preview", TenantAdminOperationKind.Read)]
    [DataRow("POST /admin/api/clients/import/preview", TenantAdminOperationKind.Read)]
    [DataRow("POST /admin/api/clients/import/", TenantAdminOperationKind.SecuritySensitiveWrite)]
    [DataRow("POST /admin/api/realms/import/", TenantAdminOperationKind.SecuritySensitiveWrite)]
    [DataRow("POST /admin/api/providers/import/", TenantAdminOperationKind.SecuritySensitiveWrite)]
    [DataRow("POST /admin/api/license", TenantAdminOperationKind.Write)]
    [DataRow("GET /admin/api/users", TenantAdminOperationKind.Read)]
    public void SensitiveEndpoints_DeclareTheExpectedOperationKind(string route, TenantAdminOperationKind expected)
    {
        using var factory = (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        var kinds = dataSource.Endpoints.OfType<RouteEndpoint>()
            .Where(e => Describe(e) == route)
            .Select(e => e.Metadata.OfType<TenantAdminOperationRequirement>().Select(r => (TenantAdminOperationKind?)r.Kind).LastOrDefault())
            .ToList();

        Assert.HasCount(1, kinds, route);
        Assert.AreEqual(expected, kinds[0], route);
    }

    private static string Describe(RouteEndpoint endpoint)
    {
        var methods = string.Join(',', endpoint.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods ?? []);
        return $"{methods} {endpoint.RoutePattern.RawText}";
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

        // Every endpoint under the tenant-admin policy must say what it is; there is no allowlist. Admin API routes
        // outside that policy (platform tenant export/import) must be platform-admin, never unguarded.
        var unmarked = new SortedSet<string>(StringComparer.Ordinal);
        var unguarded = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!pattern.StartsWith("/admin/api", StringComparison.OrdinalIgnoreCase)
                && !pattern.StartsWith("/t/{slug}/admin/api", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var policies = endpoint.Metadata.OfType<IAuthorizeData>().Select(a => a.Policy).ToList();
            if (!policies.Contains("tenant-admin"))
            {
                if (!policies.Contains("platform-admin"))
                {
                    unguarded.Add(Describe(endpoint));
                }

                continue;
            }

            if (!endpoint.Metadata.OfType<TenantAdminOperationRequirement>().Any())
            {
                unmarked.Add(Describe(endpoint));
            }
        }

        var markedOutsideTenantAdmin = dataSource.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.OfType<TenantAdminOperationRequirement>().Any()
                        && !e.Metadata.OfType<IAuthorizeData>().Any(a => a.Policy == "tenant-admin"))
            .Select(e => e.RoutePattern.RawText)
            .ToList();
        Assert.IsEmpty(markedOutsideTenantAdmin, "The marker is enforced as a tenant-admin requirement; it only belongs on tenant-admin endpoints:\n" + string.Join('\n', markedOutsideTenantAdmin));
        Assert.IsEmpty(unguarded, "Admin API endpoints outside both admin policies:\n" + string.Join('\n', unguarded));
        Assert.IsEmpty(unmarked, "Tenant-admin endpoints without WithOperation(...):\n" + string.Join('\n', unmarked));
    }
}
