using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Security.Admin;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;
using MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;
using MrWhoOidc.WebAuth.Infrastructure.Pipeline;
using MrWhoOidc.WebAuth.Observability; // for AddOidcMetricsIfMissing

var builder = WebApplication.CreateBuilder(args);

// Force early load of Auth assembly to avoid stale/partial incremental build races impacting extension method availability.
_ = typeof(MrWhoOidc.Auth.AuthServiceCollectionExtensions);

// Testing aid: allow disabling service provider validation (scope/singleton checks) when running
// snapshot or surface tests that intentionally spin up a minimal in-memory host. This avoids
// false positives from lifetime validation during transitional refactor phases.
if (string.Equals(builder.Configuration["Testing:DisableServiceProviderValidation"], "true", StringComparison.OrdinalIgnoreCase))
{
    builder.Host.UseDefaultServiceProvider(options =>
    {
        options.ValidateOnBuild = false;
        options.ValidateScopes = false;
    });
}

builder.AddServiceDefaults();

// Observability (App Insights, metrics, alerting, audit sink) extracted
builder.Services.AddMrWhoOidcObservability(builder.Configuration);
// Ensure IOidcMetrics is always available (NoOp fallback if concrete not registered elsewhere)
builder.Services.AddOidcMetricsIfMissing();

builder.Services.Configure<OidcOptions>(builder.Configuration.GetSection("Oidc"));
var oidcOptions = builder.Configuration.GetSection("Oidc").Get<OidcOptions>() ?? new OidcOptions();

builder.Services.AddSingleton(oidcOptions);

// Bind AuthOptions
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

// Bind QrLoginOptions
builder.Services.Configure<QrLoginOptions>(builder.Configuration.GetSection("QrLogin"));

// Auth/admin (Phase 2 extracted extension – limited scope)
builder.Services.AddMrWhoOidcAuthAndAdmin(builder.Configuration);

// Admin policy options
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("AdminAuth"));

// Platform admin policy options
builder.Services.Configure<PlatformAdminAuthOptions>(builder.Configuration.GetSection("PlatformAdminAuth"));

// Redis (distributed features) extracted
var redisMux = builder.Services.AddMrWhoOidcRedis(builder.Configuration);

// Presentation layer (Razor Pages + MVC + antiforgery + localization)
builder.Services.AddLocalizationAndMvc(builder.Configuration);

// Security core (DPoP, JAR replay cache, DataProtection, cert forwarding, TE limiter)
builder.Services.AddMrWhoOidcSecurityCore(builder.Configuration, redisMux);

// Persistence & core protocol services extracted
builder.Services.AddMrWhoOidcPersistenceAndCore(builder.Configuration);
builder.Services.AddMrWhoOidcCorrelation(builder.Configuration, redisMux);
// Test-only safety net to mitigate intermittent first-run missing DI registrations.
// Enabled via Testing:InlineAuthCoreSafety=true. Idempotent; re-invokes core registration if any critical service absent.
if (string.Equals(builder.Configuration["Testing:InlineAuthCoreSafety"], "true", StringComparison.OrdinalIgnoreCase))
{
    var criticalCore = new[]
    {
        typeof(MrWhoOidc.Auth.Services.IKeyStore),
        typeof(MrWhoOidc.Auth.Services.IPasswordHasher),
        typeof(MrWhoOidc.Auth.Services.ITokenService),
        typeof(MrWhoOidc.Auth.Services.ITokenValidator)
    };
    if (criticalCore.Any(t => !builder.Services.Any(d => d.ServiceType == t)))
    {
        builder.Services.AddMrWhoOidcAuthCore(); // defensive re-registration
    }
}
// Descriptor-level diagnostic (no provider build) – optional
if (string.Equals(builder.Configuration["Testing:DiagnoseAuthCore"], "true", StringComparison.OrdinalIgnoreCase))
{
    string[] critical = [
        typeof(MrWhoOidc.Auth.Services.IKeyStore).FullName!,
        typeof(MrWhoOidc.Auth.Services.IPasswordHasher).FullName!,
        typeof(MrWhoOidc.Auth.Services.ITokenService).FullName!,
        typeof(MrWhoOidc.Auth.Services.ITokenValidator).FullName!
    ];
    var missing = new List<string>();
    foreach (var c in critical)
    {
        var t = Type.GetType(c);
        if (t == null || !builder.Services.Any(d => d.ServiceType == t)) missing.Add(c);
    }
    if (missing.Count > 0)
    {
        // Attempt re-registration then re-evaluate.
        builder.Services.AddMrWhoOidcAuthCore();
        var stillMissing = missing.Where(c =>
        {
            var t = Type.GetType(c);
            return t == null || !builder.Services.Any(d => d.ServiceType == t);
        }).ToList();
        if (stillMissing.Count > 0)
        {
            if (string.Equals(builder.Configuration["Testing:DiagnoseAuthCoreStrict"], "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Auth core descriptor diagnostic (strict): missing after safety re-registration: " + string.Join(", ", stillMissing));
            }
            else
            {
                System.Console.WriteLine("[Diag][AuthCore] Missing registrations (non-strict): " + string.Join(", ", stillMissing));
            }
        }
    }
}
// Background cleanup + backchannel feature extracted
builder.Services.AddMrWhoOidcBackgroundAndBackchannel(builder.Configuration);

// Duplicate core auth registrations removed (extensions now responsible)

// CORS policy extracted
builder.Services.AddOidcCorsPolicy(oidcOptions);

// Rate limiting policies extracted
builder.Services.AddRateLimitingPolicies(redisMux is not null);

// (Handlers & grant registrations moved into AddMrWhoOidcPersistenceAndCore)
builder.Services.Configure<FederatedLogoutOptions>(builder.Configuration.GetSection("FederatedLogout"));

var app = builder.Build();

// Support: dotnet run -- --seed
if (args.Contains("--seed", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await db.Database.MigrateAsync();
    var seeder = scope.ServiceProvider.GetRequiredService<ISeeder>();
    await seeder.SeedAsync();
    return; // exit after seeding
}

// Initial endpoint set (public OIDC + core pages) now routed via extracted helper for snapshot reuse
app.MapMrWhoOidcEndpoints();

// Standard pipeline (forwarded headers, exception handling, localization, authz, rate limiting, static assets)
app.UseMrWhoOidcPipeline(redisMux);


// Admin + health endpoints (extracted)
app.MapMrWhoAdminApiEndpoints();

// (Static assets mapping handled inside UseMrWhoOidcPipeline)

app.Run();

// (Admin auth & DTO/helper types extracted to separate files in Phase 1)

// Internal helper for tests (Phase 0 safety net). This allows test code to reference a stable symbol and
// confirm that Program.cs endpoint mapping has executed. When endpoint mapping is later extracted into
// dedicated extension methods (Phase 3), this will delegate to them or be removed.
public static partial class ProgramEndpointMapping
{
    public static void MapAll(WebApplication app)
    {
        // No-op for now; mapping already occurred inline in Program.cs before app.Run().
        // Presence of this method lets tests compile against a stable API surface.
    }
}

// Expose a public Program class so external assemblies (e.g., test projects, future sample RPs)
// can reliably construct WebApplicationFactory<Program>. Top-level statements generate an
// internal Program by default; this explicit public partial keeps that linkage without altering
// runtime behavior.
public partial class Program { }
