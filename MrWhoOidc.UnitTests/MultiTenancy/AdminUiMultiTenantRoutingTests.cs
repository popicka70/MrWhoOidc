using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth;
using MrWhoOidc.UnitTests.Testing;

namespace MrWhoOidc.UnitTests.MultiTenancy;

[TestClass]
public class AdminUiMultiTenantRoutingTests
{
    [TestMethod]
    public void AdminPages_Have_TenantPrefixed_Routes_In_MultiTenant_Mode()
    {
        // Arrange: Enable multi-tenant mode
        var factory = TestWebAppFactory.CreateInMemory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Override multi-tenancy configuration to enable multi-tenant mode
                    services.Configure<MultiTenancyOptions>(options =>
                    {
                        options.Enabled = true;
                        options.DefaultTenantSlug = "default";
                    });
                });
                // Override the multi-tenancy setting at configuration level too
                builder.UseSetting("MultiTenancy:Enabled", "true");
            });

        // Act: Get the endpoint data source
        var endpoints = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var allEndpoints = endpoints.Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .ToList();

        // Get admin page endpoints - use "Admin" without leading slash to match Razor Pages patterns
        var adminEndpoints = allEndpoints
            .Where(e => e.RoutePattern.RawText?.Contains("Admin", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        
        // Log ALL admin endpoints for debugging
        Console.WriteLine($"\n=== ALL ADMIN ENDPOINTS ({adminEndpoints.Count}) ===");
        foreach (var endpoint in adminEndpoints.Take(30))
        {
            Console.WriteLine($"  {endpoint.RoutePattern.RawText}");
        }
        if (adminEndpoints.Count > 30)
        {
            Console.WriteLine($"  ... and {adminEndpoints.Count - 30} more");
        }

        // Assert: Verify we have admin endpoints
        Assert.IsTrue(adminEndpoints.Count > 0, "Should have admin endpoints");

        // Check for tenant-prefixed routes (e.g., "t/{slug}/Admin/...")
        var tenantPrefixedAdminRoutes = adminEndpoints
            .Where(e => e.RoutePattern.RawText?.StartsWith("t/{slug}/", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        // Assert: In multi-tenant mode, we should have tenant-prefixed admin routes
        Assert.IsTrue(tenantPrefixedAdminRoutes.Count > 0, 
            $"In multi-tenant mode, should have tenant-prefixed admin routes. Found {tenantPrefixedAdminRoutes.Count}");

        // Check for fallback routes (Admin/... without tenant prefix)
        var fallbackAdminRoutes = adminEndpoints
            .Where(e =>
            {
                var pattern = e.RoutePattern.RawText;
                return pattern != null &&
                       pattern.Contains("Admin", StringComparison.Ordinal) &&
                       !pattern.StartsWith("t/{slug}/", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        // Assert: Should also have fallback routes for backward compatibility
        Assert.IsTrue(fallbackAdminRoutes.Count > 0, 
            $"In multi-tenant mode, should have fallback admin routes. Found {fallbackAdminRoutes.Count}");

        // Log some examples for debugging
        Console.WriteLine($"Total admin endpoints: {adminEndpoints.Count}");
        Console.WriteLine($"Tenant-prefixed admin routes: {tenantPrefixedAdminRoutes.Count}");
        Console.WriteLine($"Fallback admin routes: {fallbackAdminRoutes.Count}");
        
        var exampleTenantRoute = tenantPrefixedAdminRoutes.FirstOrDefault();
        if (exampleTenantRoute != null)
        {
            Console.WriteLine($"Example tenant route: {exampleTenantRoute.RoutePattern.RawText}");
        }

        var exampleFallbackRoute = fallbackAdminRoutes.FirstOrDefault();
        if (exampleFallbackRoute != null)
        {
            Console.WriteLine($"Example fallback route: {exampleFallbackRoute.RoutePattern.RawText}");
        }
    }

    [TestMethod]
    public void AdminPages_Have_RootLevel_Routes_Only_In_SingleTenant_Mode()
    {
        // Arrange: Disable multi-tenant mode (single-tenant) - this is the default for TestWebAppFactory
        var factory = TestWebAppFactory.CreateInMemory();

        // Act: Get the endpoint data source
        var endpoints = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var allEndpoints = endpoints.Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .ToList();

        // Get admin page endpoints
        var adminEndpoints = allEndpoints
            .Where(e => e.RoutePattern.RawText?.Contains("Admin", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        // Assert: Verify we have admin endpoints
        Assert.IsTrue(adminEndpoints.Count > 0, "Should have admin endpoints");

        // Check for root-level routes (Admin/... without tenant prefix)
        var rootAdminRoutes = adminEndpoints
            .Where(e =>
            {
                var pattern = e.RoutePattern.RawText;
                return pattern != null &&
                       pattern.Contains("Admin", StringComparison.Ordinal) &&
                       !pattern.StartsWith("t/{slug}/", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        // Assert: Should have root-level routes in single-tenant mode
        Assert.IsTrue(rootAdminRoutes.Count > 0, 
            $"In single-tenant mode, should have root-level admin routes. Found {rootAdminRoutes.Count}");

        // Check for tenant-prefixed routes
        var tenantPrefixedAdminRoutes = adminEndpoints
            .Where(e => e.RoutePattern.RawText?.StartsWith("t/{slug}/", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        // Assert: In single-tenant mode, should NOT have tenant-prefixed admin routes
        Assert.AreEqual(0, tenantPrefixedAdminRoutes.Count, 
            $"In single-tenant mode, should NOT have tenant-prefixed admin routes. Found {tenantPrefixedAdminRoutes.Count}");

        // Log for debugging
        Console.WriteLine($"Total admin endpoints: {adminEndpoints.Count}");
        Console.WriteLine($"Root-level admin routes: {rootAdminRoutes.Count}");
        Console.WriteLine($"Tenant-prefixed admin routes: {tenantPrefixedAdminRoutes.Count}");
    }

    [TestMethod]
    public void AdminPages_Count_Matches_Expected()
    {
        // This test ensures we're not accidentally breaking admin page registration
        var factory = TestWebAppFactory.CreateInMemory();
        
        var endpoints = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var adminPageEndpoints = endpoints.Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("Admin", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        // We should have admin pages (at least platform admin + regular admin API endpoints)
        Assert.IsTrue(adminPageEndpoints.Count >= 20, 
            $"Expected at least 20 admin endpoints, found {adminPageEndpoints.Count}");
    }
}
