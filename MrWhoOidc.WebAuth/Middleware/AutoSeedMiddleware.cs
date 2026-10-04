using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Seeding;

namespace MrWhoOidc.WebAuth.Middleware;

/// <summary>
/// Seeds the default tenant with platform admin at startup or on the first request.
/// Only runs once when the database is empty (no tenants exist).
/// </summary>
public sealed class AutoSeedMiddleware
{
    private readonly RequestDelegate _next;
    private static bool _initialized = false;
    private static bool _appliedManifestUpdates = false;
    private static readonly object _lock = new();
    private static readonly SemaphoreSlim _bootstrapSemaphore = new(1, 1);

    public AutoSeedMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        AuthDbContext db,
        ISeeder seeder,
        ISeedManifestProvider seedManifestProvider,
        ISeedManifestApplier seedManifestApplier,
        IOptions<SeedManifestOptions> seedOptions,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        IIssuerBuilder issuerBuilder,
        IOptions<OidcOptions> oidcOptions,
        ILogger<AutoSeedMiddleware> logger,
        IHostEnvironment env,
        IConfiguration config)
    {
        await InitializeAsync(
            $"{context.Request.Scheme}://{context.Request.Host}", context.RequestAborted,
            db, seeder, seedManifestProvider, seedManifestApplier, seedOptions,
            tenantAccessor, multiTenancyOptions, issuerBuilder, oidcOptions, logger, env, config);
        await _next(context);
    }

    internal static async Task InitializeAsync(
        string authorityBaseUrl,
        CancellationToken cancellationToken,
        AuthDbContext db,
        ISeeder seeder,
        ISeedManifestProvider seedManifestProvider,
        ISeedManifestApplier seedManifestApplier,
        IOptions<SeedManifestOptions> seedOptions,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        IIssuerBuilder issuerBuilder,
        IOptions<OidcOptions> oidcOptions,
        ILogger<AutoSeedMiddleware> logger,
        IHostEnvironment env,
        IConfiguration config)
    {
        // Safety: auto-seeding must never run in production.
        // Requires explicit opt-in via BOTH the environment check AND
        // the feature flag being explicitly set to "true" — this prevents
        // a misconfigured ASPNETCORE_ENVIRONMENT=Development in production
        // from automatically enabling seeding of known credentials.
        var enabled = (env.IsDevelopment() || env.IsStaging())
            && string.Equals(config["Testing:EnableAutoSeed"], "true", StringComparison.OrdinalIgnoreCase);

        if (!enabled)
        {
            return;
        }

        // Fast path: if already seeded, skip
        // NOTE: We still check whether the default tenant has users on every request.
        // This avoids a failure mode where the first request is not tenant-scoped and
        // only the Tenant row is created but no users/clients are seeded.

        // Optional: seed manifest (portable JSON) can bootstrap tenants/clients for local stacks.
        // Only enabled in dev/test via the middleware gate above.
        SeedManifest? seedManifest = null;
        if (!_initialized)
        {
            await _bootstrapSemaphore.WaitAsync(cancellationToken);
            try
            {
                if (!_initialized)
                {
                    // Ensure at least one tenant exists (dev/test bootstrap)
                    var needsBootstrap = !db.Tenants.Any();
                    if (needsBootstrap)
                    {
                        seedManifest = await seedManifestProvider.TryLoadAsync(cancellationToken);

                        if (seedManifest is not null)
                        {
                            if (seedManifest.Tenants.Count > 0)
                            {
                                await seedManifestApplier.ApplyTenantsAsync(seedManifest, authorityBaseUrl, cancellationToken);
                            }

                            await seedManifestApplier.ApplyLicensesAsync(seedManifest, cancellationToken);
                        }

                        // Backwards-compatible fallback: create a default tenant if the manifest is not present.
                        if (!db.Tenants.Any())
                        {
                            var defaultSlug = multiTenancyOptions.DefaultTenantSlug ?? "default";
                            var options = oidcOptions.Value;
                            var baseUrl =
                                (!string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? options.PublicBaseUrl.TrimEnd('/') : null)
                                ?? (!string.IsNullOrWhiteSpace(options.Issuer) ? options.Issuer.TrimEnd('/') : null)
                                ?? authorityBaseUrl;

                            var issuerUri = issuerBuilder.BuildIssuer(baseUrl, defaultSlug).TrimEnd('/');

                            var defaultTenant = new Tenant
                            {
                                Slug = defaultSlug,
                                Name = "Default Tenant",
                                Description = "Default tenant created automatically",
                                IssuerUri = issuerUri,
                                Status = TenantStatus.Active,
                                MaxUsers = 100000,
                                MaxClients = 1000,
                                AdminEmail = "admin@mrwho.local",
                                BillingPlan = "Enterprise",
                                CreatedAt = DateTimeOffset.UtcNow
                            };

                            db.Tenants.Add(defaultTenant);
                            await db.SaveChangesAsync(cancellationToken);
                        }
                    }

                    _initialized = true;
                }
            }
            finally
            {
                _bootstrapSemaphore.Release();
            }
        }

        // Resolve a tenant context for seeding.
        // If the request is not tenant-scoped, fall back to the default tenant.
        var currentTenant = tenantAccessor.CurrentTenant;
        if (currentTenant is null)
        {
            var defaultSlug = multiTenancyOptions.DefaultTenantSlug ?? "default";
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == defaultSlug);
            if (tenant is not null)
            {
                tenantAccessor.SetTenant(new TenantContext
                {
                    TenantId = tenant.Id,
                    Slug = tenant.Slug,
                    Name = tenant.Name,
                    IssuerUri = tenant.IssuerUri,
                    IsMultiTenantMode = multiTenancyOptions.Enabled
                });
                currentTenant = tenantAccessor.CurrentTenant;
            }
        }

        // Seed if tenant exists but has no users yet.
        if (currentTenant is not null)
        {
            var tenantHasUsers = await db.Users.AnyAsync(u => u.TenantId == currentTenant.TenantId);
            if (!tenantHasUsers)
            {
                await seeder.SeedAsync();

                seedManifest ??= await seedManifestProvider.TryLoadAsync(cancellationToken);
                if (seedManifest is not null)
                {
                    await seedManifestApplier.ApplyLicensesAsync(seedManifest, cancellationToken);
                    await seedManifestApplier.ApplyForCurrentTenantAsync(seedManifest, cancellationToken);
                }
            }
            else if (seedOptions.Value.Enabled && seedOptions.Value.AllowUpdates)
            {
                // Dev/test quality-of-life: allow the seed manifest to update existing data (e.g., redirect URIs,
                // client secrets when OverwriteClientSecrets=true) without requiring deleting volumes.
                // Apply once per process start to avoid doing DB work on every request.
                var shouldApply = false;
                lock (_lock)
                {
                    if (!_appliedManifestUpdates)
                    {
                        _appliedManifestUpdates = true;
                        shouldApply = true;
                    }
                }

                if (shouldApply)
                {
                    try
                    {
                        seedManifest ??= await seedManifestProvider.TryLoadAsync(cancellationToken);
                        if (seedManifest is not null)
                        {
                            logger.LogInformation("Applying seed manifest updates (AllowUpdates=true) for tenant '{TenantSlug}'", currentTenant.Slug);
                            await seedManifestApplier.ApplyLicensesAsync(seedManifest, cancellationToken);
                            await seedManifestApplier.ApplyForCurrentTenantAsync(seedManifest, cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Never fail requests due to non-critical dev/test seeding.
                        logger.LogWarning(ex, "Failed to apply seed manifest updates (AllowUpdates=true)");
                    }
                }
            }
        }
    }
}

/// <summary>
/// Extension method to register AutoSeedMiddleware in the pipeline.
/// </summary>
public static class AutoSeedMiddlewareExtensions
{
    public static async Task InitializeAutoSeedAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var options = services.GetRequiredService<IOptions<OidcOptions>>();
        var baseUrl = !string.IsNullOrWhiteSpace(options.Value.PublicBaseUrl)
            ? options.Value.PublicBaseUrl
            : options.Value.Issuer;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            app.Logger.LogInformation("Auto-seeding deferred until the first request: Oidc:PublicBaseUrl and Oidc:Issuer are not configured.");
            return;
        }

        await AutoSeedMiddleware.InitializeAsync(
            baseUrl, app.Lifetime.ApplicationStopping,
            services.GetRequiredService<AuthDbContext>(),
            services.GetRequiredService<ISeeder>(),
            services.GetRequiredService<ISeedManifestProvider>(),
            services.GetRequiredService<ISeedManifestApplier>(),
            services.GetRequiredService<IOptions<SeedManifestOptions>>(),
            services.GetRequiredService<ITenantAccessor>(),
            services.GetRequiredService<IMultiTenancyOptions>(),
            services.GetRequiredService<IIssuerBuilder>(),
            options,
            services.GetRequiredService<ILogger<AutoSeedMiddleware>>(),
            app.Environment, app.Configuration);
    }

    public static IApplicationBuilder UseAutoSeed(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AutoSeedMiddleware>();
    }
}
