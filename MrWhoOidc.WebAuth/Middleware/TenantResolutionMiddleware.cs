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

        UserAccountResolution? resolvedUser = null;
        if (context.User?.Identity?.IsAuthenticated ?? false)
        {
            resolvedUser = await currentUserAccountResolver.ResolveAsync(context.User, context.RequestAborted);
        }

        // Skip tenant resolution for specific paths (health checks, platform admin, static assets)
        if (ShouldSkipTenantResolution(path))
        {
            await _next(context);
            return;
        }

        // Resolve tenant
        var tenantContext = await tenantResolver.ResolveTenantAsync(path, context.RequestAborted);

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
            "Resolved tenant: {TenantSlug} (ID: {TenantId}, Mode: {Mode})",
            tenantContext.Slug,
            tenantContext.TenantId,
            tenantContext.IsMultiTenantMode ? "multi-tenant" : "single-tenant");

        // Validate tenant access for authenticated users
        // SECURITY: Users may only access tenants they are a member of
        if (resolvedUser is not null)
        {
            var userGuid = resolvedUser.Value.UserId;

            // Check if legacy user record already scoped to this tenant
            var userTenantId = await dbContext.Users
                .Where(u => u.Id == userGuid)
                .Select(u => u.TenantId)
                .FirstOrDefaultAsync(context.RequestAborted);

            if (userTenantId != Guid.Empty && userTenantId != tenantContext.TenantId)
            {
                var hasTenantRole = await dbContext.UserRoleAssignments.AsNoTracking()
                    .Join(dbContext.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
                    .AnyAsync(x => x.a.UserId == userGuid
                                   && x.a.IsActive
                                   && x.r.IsActive
                                   && x.r.TenantId == tenantContext.TenantId,
                        context.RequestAborted);

                if (hasTenantRole)
                {
                    _logger.LogDebug("Role assignment permits cross-tenant access for user {UserId} into tenant {TenantId}", userGuid, tenantContext.TenantId);
                }
                else
                {
                    _logger.LogWarning(
                        "SECURITY: User {UserId} attempted to access tenant {RequestedTenant} ({RequestedSlug}) but belongs to tenant {UserTenant}. Request denied.",
                        userGuid, tenantContext.TenantId, tenantContext.Slug, userTenantId);

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "text/html";
                    await context.Response.WriteAsync(@"
<!DOCTYPE html>
<html>
<head>
    <title>Access Denied</title>
    <style>
        body { font-family: system-ui; max-width: 600px; margin: 100px auto; padding: 20px; text-align: center; }
        h1 { color: #dc3545; }
        .error-icon { font-size: 48px; }
        .message { margin: 20px 0; color: #666; }
        a { color: #0d6efd; text-decoration: none; }
    </style>
</head>
<body>
    <div class='error-icon'>🚫</div>
    <h1>Access Denied</h1>
    <p class='message'>You do not have permission to access this tenant.</p>
    <p class='message'>You can only access resources within your assigned tenant.</p>
    <p><a href='/'>Return to Home</a></p>
</body>
</html>");
                    return;
                }
            }
        }
        else if (context.User?.Identity?.IsAuthenticated ?? false)
        {
            _logger.LogWarning("Authenticated principal could not be linked to a user account; denying access.");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Continue pipeline
        await _next(context);
    }

    /// <summary>
    /// Determines if tenant resolution should be skipped for the given path.
    /// Skips: health checks, platform admin routes, static assets, swagger, etc.
    /// </summary>
    private static bool ShouldSkipTenantResolution(string path)
    {
        var lowerPath = path.ToLowerInvariant();

        return lowerPath.StartsWith("/health") ||
               lowerPath.StartsWith("/platform-admin") ||
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
