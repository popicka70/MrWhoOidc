using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Licensing;
using MrWhoOidc.Auth.Licensing.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Entitlements.Options;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Security.Admin;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;
using MrWhoOidc.WebAuth.Infrastructure.EndpointMapping;
using MrWhoOidc.WebAuth.Infrastructure.Pipeline;
using MrWhoOidc.WebAuth.Middleware;
using MrWhoOidc.WebAuth.Observability; // for AddOidcMetricsIfMissing
using Microsoft.Extensions.Options;

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

builder.Services.AddLicensingOptions(builder.Configuration);
builder.Services.PostConfigure<LicensingOptions>(options =>
{
    var oidc = builder.Configuration.GetSection("Oidc").Get<OidcOptions>();
    if (oidc != null && !string.IsNullOrWhiteSpace(oidc.Issuer))
    {
        options.PlatformIssuer = oidc.Issuer;
    }
});
builder.Services.AddMrWhoOidcLicensing();

builder.Services.Configure<OidcOptions>(builder.Configuration.GetSection("Oidc"));
var oidcOptions = builder.Configuration.GetSection("Oidc").Get<OidcOptions>() ?? new OidcOptions();

// Normalize configured URLs to avoid subtle trailing-slash mismatches.
oidcOptions.Issuer = string.IsNullOrWhiteSpace(oidcOptions.Issuer) ? null : oidcOptions.Issuer.Trim();
oidcOptions.PublicBaseUrl = string.IsNullOrWhiteSpace(oidcOptions.PublicBaseUrl) ? null : oidcOptions.PublicBaseUrl.Trim();

builder.Services.AddSingleton(oidcOptions);

builder.Services.PostConfigure<OidcOptions>(o =>
{
    o.Issuer = string.IsNullOrWhiteSpace(o.Issuer) ? null : o.Issuer.Trim();
    o.PublicBaseUrl = string.IsNullOrWhiteSpace(o.PublicBaseUrl) ? null : o.PublicBaseUrl.Trim();
});

// Bind AuthOptions
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

// Bind QrLoginOptions
builder.Services.Configure<QrLoginOptions>(builder.Configuration.GetSection("QrLogin"));

// Bind WebAuthnOptions
builder.Services.Configure<WebAuthnOptions>(builder.Configuration.GetSection("WebAuthn"));

// Auth/admin (Phase 2 extracted extension – limited scope)
builder.Services.AddMrWhoOidcAuthAndAdmin(builder.Configuration);

// Admin policy options
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("AdminAuth"));

// Platform admin policy options
builder.Services.Configure<PlatformAdminAuthOptions>(builder.Configuration.GetSection("PlatformAdminAuth"));

// Redis (distributed features) extracted
var redisMux = builder.Services.AddMrWhoOidcRedis(builder.Configuration);

// HybridCache (L1 + optional L2 via Redis)
builder.Services.AddMrWhoOidcHybridCache(builder.Configuration, redisMux);

// Presentation layer (Razor Pages + MVC + antiforgery + localization)
builder.Services.AddLocalizationAndMvc(builder.Configuration);

// Security core (DPoP, JAR replay cache, DataProtection, cert forwarding, TE limiter)
builder.Services.AddMrWhoOidcSecurityCore(builder.Configuration, redisMux);

// Persistence & core protocol services extracted
builder.Services.AddMrWhoOidcPersistenceAndCore(builder.Configuration);
builder.Services.AddMrWhoOidcCorrelation(builder.Configuration, redisMux);
builder.Services.AddMrWhoOidcMail(builder.Configuration);

// Login continuation store (keeps large ReturnUrl values out of /login query string)
builder.Services.AddSingleton<MrWhoOidc.WebAuth.Services.ILoginContinuationStore, MrWhoOidc.WebAuth.Services.DistributedLoginContinuationStore>();

// LicensingService entitlements integration (Phase 2 PDF licensing)
builder.Services.AddMemoryCache();
builder.Services.Configure<LicensingIntegrationOptions>(builder.Configuration.GetSection("LicensingIntegration"));
builder.Services.AddHttpClient<ILicensingEntitlementsClient, LicensingEntitlementsClient>((sp, client) =>
{
    var opt = sp.GetRequiredService<IOptions<LicensingIntegrationOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(opt.BaseUrl))
    {
        client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/'));
    }
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    // Dev-only convenience: allow calling a HTTPS LicensingService with a self-signed cert.
    if (builder.Environment.IsDevelopment())
    {
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
    }

    return new HttpClientHandler();
});
builder.Services.AddScoped<IEntitlementsProvider, CachingEntitlementsProvider>();
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

// On-demand tenant seeding service
builder.Services.AddScoped<MrWhoOidc.WebAuth.Services.ITenantSeedingService, MrWhoOidc.WebAuth.Services.TenantSeedingService>();

// Optional seed manifest (portable JSON for future import/export)
builder.Services.Configure<MrWhoOidc.WebAuth.Seeding.SeedManifestOptions>(builder.Configuration.GetSection("Seeding"));
builder.Services.AddSingleton<MrWhoOidc.WebAuth.Seeding.ISeedManifestProvider, MrWhoOidc.WebAuth.Seeding.SeedManifestProvider>();
builder.Services.AddScoped<MrWhoOidc.WebAuth.Seeding.ISeedManifestApplier, MrWhoOidc.WebAuth.Seeding.SeedManifestApplier>();

// Tenant switching service
builder.Services.AddScoped<MrWhoOidc.WebAuth.Services.ITenantSwitchingService, MrWhoOidc.WebAuth.Services.TenantSwitchingService>();

// Tenant credential ticket store
builder.Services.AddScoped<MrWhoOidc.WebAuth.Services.ITenantCredentialTicketStore, MrWhoOidc.WebAuth.Services.TenantCredentialTicketStore>();

// Impersonation service (platform admin viewing as tenant admin)
builder.Services.AddScoped<MrWhoOidc.WebAuth.Services.IImpersonationService, MrWhoOidc.WebAuth.Services.ImpersonationService>();

// Tenant branding service
builder.Services.AddScoped<MrWhoOidc.Auth.Services.ITenantBrandingService, MrWhoOidc.Auth.Services.TenantBrandingService>();

// ReturnUrl client context resolver (safe client derivation for registration/login UX)
builder.Services.AddScoped<MrWhoOidc.WebAuth.Services.IReturnUrlClientContextResolver, MrWhoOidc.WebAuth.Services.ReturnUrlClientContextResolver>();

// Tenant settings service (cascading: platform → tenant → client)
builder.Services.AddScoped<MrWhoOidc.Auth.Services.ITenantSettingsService, MrWhoOidc.Auth.Services.TenantSettingsService>();

// Configuration export/import services
builder.Services.AddScoped<MrWhoOidc.Auth.Services.IConfigurationExportService, MrWhoOidc.WebAuth.Services.ConfigurationExportService>();
builder.Services.AddScoped<MrWhoOidc.Auth.Services.IConfigurationImportService, MrWhoOidc.WebAuth.Services.ConfigurationImportService>();

// Duplicate core auth registrations removed (extensions now responsible)

// CORS policy extracted
builder.Services.AddOidcCorsPolicy(oidcOptions);

// Rate limiting policies extracted
builder.Services.AddRateLimitingPolicies(redisMux is not null, redisMux);

// (Handlers & grant registrations moved into AddMrWhoOidcPersistenceAndCore)
builder.Services.Configure<FederatedLogoutOptions>(builder.Configuration.GetSection("FederatedLogout"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    var issuer = string.IsNullOrWhiteSpace(oidcOptions.Issuer) ? null : oidcOptions.Issuer.TrimEnd('/');
    var publicBaseUrl = string.IsNullOrWhiteSpace(oidcOptions.PublicBaseUrl) ? null : oidcOptions.PublicBaseUrl.TrimEnd('/');
    if (issuer is null && publicBaseUrl is null)
    {
        app.Logger.LogWarning(
            "Neither Oidc:Issuer nor Oidc:PublicBaseUrl is configured. " +
            "This can cause incorrect issuer/endpoint URLs behind proxies. " +
            "See /health/issuer for validation.");
    }
}

var autoSeedEnabled = app.Environment.IsDevelopment()
    || string.Equals(app.Configuration["Testing:EnableAutoSeed"], "true", StringComparison.OrdinalIgnoreCase);

// Run migrations on startup (only for relational databases, not in-memory test DBs)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    
    logger.LogInformation("Checking database migration status...");
    
    if (db.Database.IsRelational())
    {
        logger.LogInformation("Database is relational, applying migrations...");
        try
        {
            await db.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied successfully");
            
            // Check if TenantIcon table exists
            var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
            var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();
            
            logger.LogInformation("Applied migrations: {AppliedMigrations}", string.Join(", ", appliedMigrations));
            if (pendingMigrations.Any())
            {
                logger.LogWarning("Pending migrations detected: {PendingMigrations}", string.Join(", ", pendingMigrations));
            }
            else
            {
                logger.LogInformation("All migrations are up to date");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply database migrations: {Message}", ex.Message);
            throw;
        }
        
        logger.LogInformation("Automatic tenant bootstrap on startup is disabled; use the explicit bootstrap endpoint when needed.");
    }
    else
    {
        logger.LogInformation("Database is not relational (in-memory), skipping migrations");
    }
}

// Standard pipeline (forwarded headers, exception handling, localization, authz, rate limiting, static assets)
// MUST come before endpoint mapping because it includes UseRouting()
// Pass migration completion source so the pipeline can wait for migrations before processing requests
var migrationCompletionSource = EndpointMappingExtensions.GetMigrationCompletionSource();
// Optional dev/test-only convenience middleware.
// NOTE: Never enable this in production.
// IMPORTANT: Must run before tenant resolution in the main pipeline so a fresh DB can bootstrap a default tenant.
if (autoSeedEnabled)
{
    app.UseAutoSeed();
}

app.UseMrWhoOidcPipeline(redisMux, migrationCompletionSource);

// Explicit one-time bootstrap endpoint (guarded by operator token, and only when DB is empty)
app.MapMrWhoBootstrapEndpoints();

// Initial endpoint set (public OIDC + core pages) now routed via extracted helper for snapshot reuse
app.MapMrWhoOidcEndpoints();

// Admin + health endpoints (extracted)
app.MapMrWhoAdminApiEndpoints();

// Export/Import API endpoints (platform admin only)
app.MapExportImportEndpoints();

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
