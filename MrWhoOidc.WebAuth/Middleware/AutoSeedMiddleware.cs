using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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
    private static bool _seeded = false;
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
        OidcOptions oidcOptions,
        IHostEnvironment env,
        IConfiguration config)
    {
        // Safety: auto-seeding must never run in production unless explicitly enabled.
        var enabled = env.IsDevelopment()
            || string.Equals(config["Testing:EnableAutoSeed"], "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(config["AutoSeed:Enabled"], "true", StringComparison.OrdinalIgnoreCase);

        if (!enabled)
        {
            await _next(context);
            return;
        }

        // Fast path: if already seeded, skip
        if (_seeded)
        {
            await _next(context);
            return;
        }

        // Double-check lock to ensure seeding only happens once
        lock (_lock)
        {
            if (_seeded)
            {
                // Another thread beat us to it
                // Continue to next middleware
            }
            else
            {
                // Check if database needs seeding (no tenants exist)
                var needsSeeding = !db.Tenants.Any();

                if (needsSeeding)
                {
                    // Create default tenant first (synchronously for simplicity in lock)
                    var defaultSlug = multiTenancyOptions.DefaultTenantSlug ?? "default";
                    var baseUrl = !string.IsNullOrWhiteSpace(oidcOptions.PublicBaseUrl)
                        ? oidcOptions.PublicBaseUrl.TrimEnd('/')
                        : $"{context.Request.Scheme}://{context.Request.Host}";
                    var issuerUri = multiTenancyOptions.Enabled
                        ? $"{baseUrl}/t/{defaultSlug}"
                        : baseUrl;

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

                    // Set tenant context for seeding
                    // Note: We need to run seeding in async context outside the lock
                    _seeded = true; // Mark as seeded before async work
                }
                else
                {
                    // Database already has tenants, no seeding needed
                    _seeded = true;
                }
            }
        }

        // If we just created a tenant, run the seeder now (outside lock, async)
        if (!_seeded)
        {
            // This should never happen due to logic above, but kept for safety
            await _next(context);
            return;
        }

        // Check if we need to run the seeder (tenant exists but no users)
        var currentTenant = tenantAccessor.CurrentTenant;
        if (currentTenant != null)
        {
            var tenantHasUsers = await db.Users.AnyAsync(u => u.TenantId == currentTenant.TenantId);
            if (!tenantHasUsers)
            {
                // Run seeder for this tenant
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
