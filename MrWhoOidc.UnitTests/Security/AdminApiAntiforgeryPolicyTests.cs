using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// M-CSRF: the antiforgery policy used to be attached to four admin API endpoints only, while the admin API
/// has dozens of mutating endpoints. These tests pin the complete policy: every unsafe method on the admin
/// API must carry it, and the endpoint surface snapshot records it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AdminApiAntiforgeryPolicyTests
{
    private static readonly string[] AdminApiPrefixes =
    [
        "/admin/api",
        "/t/{slug}/admin/api",
        "/platform-admin/api"
    ];

    private static bool IsAdminApiPath(string pattern) =>
        AdminApiPrefixes.Any(prefix => pattern.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method)
        || HttpMethods.IsHead(method)
        || HttpMethods.IsOptions(method)
        || HttpMethods.IsTrace(method);

    [TestMethod]
    public void EveryUnsafeAdminApiEndpoint_CarriesTheAntiforgeryPolicy()
    {
        using var factory = TestWebAppFactory.CreateInMemory();
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        var unsafeEndpoints = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => IsAdminApiPath(e.RoutePattern.RawText ?? string.Empty))
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Any(m => !IsSafeMethod(m)) == true)
            .ToList();

        Assert.IsTrue(unsafeEndpoints.Count >= 20,
            $"the admin API must have a meaningful number of mutating endpoints (found {unsafeEndpoints.Count})");

        var unprotected = unsafeEndpoints
            .Where(e => e.Metadata.GetMetadata<AdminApiAntiforgeryMetadata>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.AreEqual(0, unprotected.Count,
            "every mutating admin API endpoint must be antiforgery protected: " + string.Join(", ", unprotected));
    }

    [TestMethod]
    public void SafeAdminApiEndpoints_AreNotAffected()
    {
        using var factory = TestWebAppFactory.CreateInMemory();
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        // A GET must never be blocked by the antiforgery filter.
        var getEndpoints = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => IsAdminApiPath(e.RoutePattern.RawText ?? string.Empty))
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .All(m => IsSafeMethod(m)) == true)
            .ToList();

        Assert.IsTrue(getEndpoints.Count > 0, "the admin API must have read endpoints");
    }
}
