using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.WebAuth.Middleware;

/// <summary>
/// Middleware that automatically seeds the default tenant with platform admin on first request.
/// Only runs once when the database is empty (no tenants exist).
/// </summary>
public sealed class AutoSeedMiddleware
{
    private readonly RequestDelegate _next;
    private static bool _initialized = false;
    private static readonly object _lock = new();

    public AutoSeedMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        AuthDbContext db,
        ISeeder seeder,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        IIssuerBuilder issuerBuilder,
        IOptions<OidcOptions> oidcOptions,
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

        // Double-check lock to ensure seeding only happens once
        lock (_lock)
        {
            if (_initialized)
            {
                // Another thread already initialized tenant bootstrap.
            }
            else
            {
                // Ensure at least one tenant exists (dev/test bootstrap)
                var needsBootstrap = !db.Tenants.Any();
                if (needsBootstrap)
                {
                    // Create default tenant first (synchronously for simplicity in lock)
                    var defaultSlug = multiTenancyOptions.DefaultTenantSlug ?? "default";
                    var options = oidcOptions.Value;
                    var baseUrl =
                        (!string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? options.PublicBaseUrl.TrimEnd('/') : null)
                        ?? (!string.IsNullOrWhiteSpace(options.Issuer) ? options.Issuer.TrimEnd('/') : null)
                        ?? $"{context.Request.Scheme}://{context.Request.Host}";

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
                    db.SaveChanges(); // Synchronous save in lock
                }

                _initialized = true;
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
