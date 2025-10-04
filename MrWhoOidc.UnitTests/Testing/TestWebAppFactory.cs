using Microsoft.AspNetCore.Mvc.Testing;
using MrWhoOidc.WebAuth; // Program
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.UnitTests.Testing;

/// <summary>
/// Centralized factory helpers for building WebAuth hosts in tests.
/// Tiers:
///  - InMemory (surface/snapshot/safety tests) -> forces EF InMemory + skips migrations.
///  - RealDb (integration) -> expects an AUTHDB__CONNECTIONSTRING or ConnectionStrings:authdb.
/// </summary>
internal static class TestWebAppFactory
{
    internal static WebApplicationFactory<Program> CreateInMemory()
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                // EARLY flags so Program.cs sees them during registration
                b.UseSetting("Testing:UseInMemoryAuthDb", "true");
                b.UseSetting("Testing:SkipAuthMigrations", "true");
                b.UseSetting("Testing:InlineAuthCoreSafety", "true");
                b.UseSetting("Testing:DisableServiceProviderValidation", "true");
                b.UseSetting("Testing:ValidateAuthCore", "true");
                b.UseSetting("Testing:DiagnoseAuthCore", "false");
                b.UseSetting("Testing:DisableStaticAssets", "true");
                // Multi-tenancy: single-tenant mode for tests
                b.UseSetting("MultiTenancy:Enabled", "false");
                b.UseSetting("MultiTenancy:DefaultTenantSlug", "default");
                // Provide a fake connection string (will be ignored because of in-memory flag)
                b.UseSetting("ConnectionStrings:authdb", "Host=localhost;Database=fake;Username=fake;Password=fake");
                b.ConfigureAppConfiguration((ctx, cfg) =>
                {
                    var dict = new Dictionary<string, string?>
                    {
                        ["Testing:UseInMemoryAuthDb"] = "true",
                        ["Testing:SkipAuthMigrations"] = "true",
                        ["Testing:AllowInMemoryFallback"] = "true",
                        ["Testing:InlineAuthCoreSafety"] = "true",
                        ["Testing:DisableServiceProviderValidation"] = "true",
                        ["Testing:ValidateAuthCore"] = "true",
                        ["Testing:DiagnoseAuthCore"] = "false",
                        ["Testing:DisableStaticAssets"] = "true",
                        ["MultiTenancy:Enabled"] = "false",
                        ["MultiTenancy:DefaultTenantSlug"] = "default",
                        ["ConnectionStrings:authdb"] = "Host=localhost;Database=fake;Username=fake;Password=fake"
                    };
                    cfg.AddInMemoryCollection(dict);
                });
                
                // Seed default tenant for tests
                b.ConfigureServices((context, services) =>
                {
                    // Use a hosted service to seed the tenant after app starts
                    services.AddHostedService<DefaultTenantSeedingService>();
                });
            });

    internal static WebApplicationFactory<Program> CreateRealDb()
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                // Don't set in-memory flag here. Expect a real connection string.
                var cs = Environment.GetEnvironmentVariable("AUTHDB__CONNECTIONSTRING")
                         ?? Environment.GetEnvironmentVariable("AUTHDB_CONNECTIONSTRING")
                         ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
                         ?? "";
                if (string.IsNullOrWhiteSpace(cs))
                {
                    throw new InvalidOperationException("RealDb test host requires AUTHDB__CONNECTIONSTRING environment variable.");
                }
                b.UseSetting("ConnectionStrings:authdb", cs);
                // Allow migrations & seeding (default) unless an override is provided
                b.ConfigureAppConfiguration((ctx, cfg) =>
                {
                    var dict = new Dictionary<string, string?>
                    {
                        ["Testing:ValidateAuthCore"] = "true",
                        ["Testing:InlineAuthCoreSafety"] = "true"
                    };
                    cfg.AddInMemoryCollection(dict);
                });
            });
}

/// <summary>
/// Background service that seeds the default tenant once on startup.
/// Uses a static flag to ensure seeding only happens once per test run (shared in-memory database).
/// </summary>
internal sealed class DefaultTenantSeedingService : IHostedService
{
    private static int _seeded = 0;
    private readonly IServiceProvider _serviceProvider;

    public DefaultTenantSeedingService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Use Interlocked.CompareExchange to ensure only one thread/instance seeds the tenant
        if (Interlocked.CompareExchange(ref _seeded, 1, 0) == 0)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            
            var defaultTenantId = new Guid("00000000-0000-0000-0000-000000000001");
            db.Tenants.Add(new Tenant
            {
                Id = defaultTenantId,
                Slug = "default",
                Name = "Default Tenant",
                IssuerUri = "https://localhost:5001",
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
