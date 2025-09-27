using Microsoft.AspNetCore.Mvc.Testing;
using MrWhoOidc.WebAuth; // Program
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

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
                        ["ConnectionStrings:authdb"] = "Host=localhost;Database=fake;Username=fake;Password=fake"
                    };
                    cfg.AddInMemoryCollection(dict);
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
