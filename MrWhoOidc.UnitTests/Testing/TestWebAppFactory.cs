using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using MrWhoOidc.WebAuth; // Program
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;
using Microsoft.EntityFrameworkCore;

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
    {
        // Use a unique database name per factory instance to avoid conflicts when tests run in parallel
        var uniqueDbName = $"TestDb_{Guid.NewGuid():N}";

        return new WebApplicationFactory<Program>()
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
                b.UseSetting("Testing:DisableBackgroundServices", "true"); // Skip background services for faster tests
                // Store unique DB name for later use
                b.UseSetting("Testing:InMemoryDbName", uniqueDbName);
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
                        ["Testing:DisableBackgroundServices"] = "true", // Skip background services for faster tests
                        ["Testing:InMemoryDbName"] = uniqueDbName,
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

                // Override services AFTER all other registration (this runs last)
                b.ConfigureTestServices(services =>
                {
                    // Override TenantAccessor to automatically set default tenant for test scopes
                    services.AddScoped<ITenantAccessor>(sp =>
                    {
                        var db = sp.GetRequiredService<AuthDbContext>();
                        var logger = sp.GetService<ILogger<TestTenantAccessor>>();
                        return new TestTenantAccessor(db, new Guid("00000000-0000-0000-0000-000000000001"), logger);
                    });
                });
            });
    }

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
/// Checks if tenant already exists in database before seeding.
/// </summary>
internal sealed class DefaultTenantSeedingService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;

    public DefaultTenantSeedingService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var defaultTenantId = new Guid("00000000-0000-0000-0000-000000000001");

        // Check if tenant already exists (handles shared in-memory database across tests)
        var existingTenant = await db.Tenants.FindAsync([defaultTenantId], cancellationToken);
        if (existingTenant == null)
        {
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

/// <summary>
/// Test-specific implementation of ITenantAccessor that automatically loads
/// and sets the default tenant context when first accessed.
/// This ensures integration tests have proper tenant context even when
/// services are resolved outside of HTTP request pipeline.
/// </summary>
public sealed class TestTenantAccessor : ITenantAccessor
{
    private readonly AuthDbContext _db;
    private readonly Guid _defaultTenantId;
    private readonly ILogger<TestTenantAccessor>? _logger;
    private TenantContext? _currentTenant;
    private bool _initialized;
    private readonly object _lock = new object();

    public TestTenantAccessor(AuthDbContext db, Guid defaultTenantId, ILogger<TestTenantAccessor>? logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _defaultTenantId = defaultTenantId;
        _logger = logger;
    }

    public TenantContext? CurrentTenant
    {
        get
        {
            if (!_initialized)
            {
                lock (_lock)
                {
                    if (!_initialized)
                    {
                        try
                        {
                            // EnsureCreated is synchronous and safe to call multiple times
                            _db.Database.EnsureCreated();

                            // Load tenant from database
                            var tenant = _db.Tenants.FirstOrDefault(t => t.Id == _defaultTenantId);

                            if (tenant != null)
                            {
                                _currentTenant = new TenantContext
                                {
                                    TenantId = tenant.Id,
                                    Slug = tenant.Slug,
                                    Name = tenant.Name,
                                    IssuerUri = tenant.IssuerUri,
                                    IsMultiTenantMode = false
                                };

                                _logger?.LogDebug("TestTenantAccessor initialized with default tenant: {Slug} (ID: {TenantId})",
                                    tenant.Slug, tenant.Id);
                            }
                            else
                            {
                                _logger?.LogWarning("Default tenant {TenantId} not found in database", _defaultTenantId);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to load default tenant {TenantId}", _defaultTenantId);
                        }
                        finally
                        {
                            _initialized = true;
                        }
                    }
                }
            }

            return _currentTenant;
        }
    }

    public void SetTenant(TenantContext context)
    {
        lock (_lock)
        {
            _currentTenant = context;
            _initialized = true;
        }
    }
}
