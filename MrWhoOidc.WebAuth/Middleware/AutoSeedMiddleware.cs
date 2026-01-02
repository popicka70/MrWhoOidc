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
/// Middleware that automatically seeds the default tenant with platform admin on first request.
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
        // Safety: auto-seeding must never run in production.
        var enabled = env.IsDevelopment()
            || string.Equals(config["Testing:EnableAutoSeed"], "true", StringComparison.OrdinalIgnoreCase);

        if (!enabled)
        {
            await _next(context);
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
            await _bootstrapSemaphore.WaitAsync(context.RequestAborted);
            try
            {
                if (!_initialized)
                {
                    // Ensure at least one tenant exists (dev/test bootstrap)
                    var needsBootstrap = !db.Tenants.Any();
                    if (needsBootstrap)
                    {
                        seedManifest = await seedManifestProvider.TryLoadAsync(context.RequestAborted);

                        var authorityBaseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
                        if (seedManifest is not null)
                        {
                            if (seedManifest.Tenants.Count > 0)
                            {
                                await seedManifestApplier.ApplyTenantsAsync(seedManifest, authorityBaseUrl, context.RequestAborted);
                            }

                            await seedManifestApplier.ApplyLicensesAsync(seedManifest, context.RequestAborted);
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
                            await db.SaveChangesAsync(context.RequestAborted);
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

                seedManifest ??= await seedManifestProvider.TryLoadAsync(context.RequestAborted);
                if (seedManifest is not null)
                {
                    await seedManifestApplier.ApplyLicensesAsync(seedManifest, context.RequestAborted);
                    await seedManifestApplier.ApplyForCurrentTenantAsync(seedManifest, context.RequestAborted);
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
                        seedManifest ??= await seedManifestProvider.TryLoadAsync(context.RequestAborted);
                        if (seedManifest is not null)
                        {
                            logger.LogInformation("Applying seed manifest updates (AllowUpdates=true) for tenant '{TenantSlug}'", currentTenant.Slug);
                            await seedManifestApplier.ApplyLicensesAsync(seedManifest, context.RequestAborted);
                            await seedManifestApplier.ApplyForCurrentTenantAsync(seedManifest, context.RequestAborted);
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

        await _next(context);
    }
}

/// <summary>
/// Extension method to register AutoSeedMiddleware in the pipeline.
/// </summary>
public static class AutoSeedMiddlewareExtensions
{
    public static IApplicationBuilder UseAutoSeed(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AutoSeedMiddleware>();
    }
}
