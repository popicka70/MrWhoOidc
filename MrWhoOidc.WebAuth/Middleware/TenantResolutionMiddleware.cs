using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Middleware;

/// <summary>
/// Middleware that resolves the current tenant early in the request pipeline.
/// Must be registered after routing middleware but before endpoint execution.
/// 
/// Behavior:
/// - Single-tenant mode: Always resolves to default tenant
/// - Multi-tenant mode: Parses path for /t/{slug} and resolves tenant
/// - Sets TenantContext via ITenantAccessor for downstream services
/// - Returns 404 if tenant cannot be resolved in multi-tenant mode
/// </summary>
public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(
        RequestDelegate next,
        ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantResolver tenantResolver,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions options,
        HybridCache cache,
        AuthDbContext dbContext,
        ICurrentUserAccountResolver currentUserAccountResolver)
    {
        var path = context.Request.Path.Value ?? "/";

        _logger.LogDebug("🌐 [TenantResolution] START - Path={Path}, MultiTenantEnabled={Enabled}, DefaultSlug={DefaultSlug}",
            path, options.Enabled, options.DefaultTenantSlug);

        UserAccountResolution? resolvedUser = null;
        if (context.User?.Identity?.IsAuthenticated ?? false)
        {
            resolvedUser = await currentUserAccountResolver.ResolveAsync(context.User, context.RequestAborted);
            _logger.LogDebug("🌐 [TenantResolution] Authenticated user resolved: UserId={UserId}, UserAccountId={UserAccountId}",
                resolvedUser?.UserId, resolvedUser?.UserAccountId);
        }

        // Skip tenant resolution for specific paths (health checks, platform admin, static assets)
        if (ShouldSkipTenantResolution(path))
        {
            _logger.LogDebug("🌐 [TenantResolution] SKIPPED - Path is excluded from tenant resolution");
            await _next(context);
            return;
        }

        // Resolve tenant
        var tenantContext = await tenantResolver.ResolveTenantAsync(path, context.RequestAborted);

        _logger.LogDebug("🌐 [TenantResolution] Resolved tenant: {TenantName} ({TenantSlug}), TenantId={TenantId}, IsMultiTenant={IsMultiTenant}",
            tenantContext?.Name, tenantContext?.Slug, tenantContext?.TenantId, tenantContext?.IsMultiTenantMode);

        if (tenantContext == null)
        {
            // Check if path has /t/{slug} prefix
            var hasPrefix = path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase);

            if (hasPrefix)
            {
                // Path has /t/{slug} but tenant not found
                // Redirect to NotFound page with tenant context determined from authenticated user
                _logger.LogWarning("Tenant not found for path: {Path}", path);

                // Try to determine tenant from authenticated user
                if (resolvedUser is not null)
                {
                    var userCacheKey = resolvedUser.Value.UserId.ToString();

                    var userTenantSlug = await cache.GetOrCreateAsync(
                        $"user:tenant:slug:{userCacheKey}",
                        async cancel =>
                        {
                            var result = await (from u in dbContext.Users
                                                join t in dbContext.Tenants on u.TenantId equals t.Id
                                                where u.Id == resolvedUser.Value.UserId
                                                select t.Slug)
                                .FirstOrDefaultAsync(cancel);
                            return result; // Can be null
                        },
                        new HybridCacheEntryOptions
                        {
                            Expiration = TimeSpan.FromMinutes(2),
                            LocalCacheExpiration = TimeSpan.FromMinutes(2)
                        },
                        tags: new[] { "user-tenant-mapping", $"user:{userCacheKey}" },
                        cancellationToken: context.RequestAborted
                    );

                    if (userTenantSlug != null)
                    {
                        context.Response.Redirect($"/t/{userTenantSlug}/NotFound", permanent: false);
                        return;
                    }
                }

                // Fallback: redirect to default tenant NotFound page or generic NotFound
                if (!string.IsNullOrEmpty(options.DefaultTenantSlug))
                {
                    context.Response.Redirect($"/t/{options.DefaultTenantSlug}/NotFound", permanent: false);
                }
                else
                {
                    // No default tenant, redirect to non-tenant NotFound
                    context.Response.Redirect("/NotFound", permanent: false);
                }
                return;
            }
            else
            {
                // No /t/{slug} prefix but default tenant not found - config error, return 500
                _logger.LogError("Default tenant resolution failed for path: {Path}. Slug: {Slug}",
                    path, options.DefaultTenantSlug);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("Server configuration error: default tenant not found.");
                return;
            }
        }

        // Set tenant context for this request
        tenantAccessor.SetTenant(tenantContext);

        _logger.LogDebug(
            "🌐 [TenantResolution] Tenant context SET: {TenantSlug} (ID: {TenantId}, Mode: {Mode})",
            tenantContext.Slug,
            tenantContext.TenantId,
            tenantContext.IsMultiTenantMode ? "multi-tenant" : "single-tenant");

        // Membership is enforced by TenantMembershipMiddleware after UseAuthentication(): this middleware runs
        // before authentication (the auth handlers need the tenant), so context.User is still anonymous here.

        // Continue pipeline
        await _next(context);
    }

    /// <summary>
    /// Determines if tenant resolution should be skipped for the given path.
    /// Skips: health checks, platform admin routes, static assets, swagger, etc.
    /// </summary>
    /// <summary>
    /// Exactly /t/{slug}/notfound. A bare EndsWith("/notfound") also matched any route whose last segment was a
    /// user-chosen "notfound" (e.g. /t/b/admin/api/scopes/notfound), skipping tenant resolution and the H4
    /// membership check for it.
    /// </summary>
    internal static bool IsTenantNotFoundPage(string lowerPath)
    {
        var segments = lowerPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 && segments[0] == "t" && segments[2] == "notfound";
    }

    private static bool ShouldSkipTenantResolution(string path)
    {
        var lowerPath = path.ToLowerInvariant();

        return lowerPath.StartsWith("/health") ||
               lowerPath.StartsWith("/bootstrap") ||
             lowerPath.StartsWith("/api/bootstrap") ||
               lowerPath.StartsWith("/platform-admin") ||
               lowerPath.StartsWith("/notfound") ||
               IsTenantNotFoundPage(lowerPath) ||
               lowerPath.StartsWith("/_") ||
               lowerPath.StartsWith("/swagger") ||
               lowerPath.StartsWith("/api/platform") ||
               lowerPath.StartsWith("/css") ||
               lowerPath.StartsWith("/js") ||
               lowerPath.StartsWith("/lib") ||
               lowerPath.StartsWith("/favicon.ico");
    }
}

/// <summary>
/// Extension methods for registering tenant resolution middleware.
/// </summary>
public static class TenantResolutionMiddlewareExtensions
{
    /// <summary>
    /// Adds tenant resolution middleware to the pipeline.
    /// Should be called after UseRouting() but before UseEndpoints().
    /// </summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TenantResolutionMiddleware>();
    }
}
