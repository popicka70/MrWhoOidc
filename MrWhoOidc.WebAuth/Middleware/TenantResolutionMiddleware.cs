using Microsoft.AspNetCore.Http;using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;

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
        IMultiTenancyOptions options)
    {
        var path = context.Request.Path.Value ?? "/";
        
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
                if (context.User?.Identity?.IsAuthenticated ?? false)
                {
                    var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (!string.IsNullOrEmpty(userId))
                    {
                        // Inject AuthDbContext to look up user's tenant
                        var dbContext = context.RequestServices.GetRequiredService<MrWhoOidc.Auth.Persistence.AuthDbContext>();
                        var userTenant = await (from u in dbContext.Users
                                                join t in dbContext.Tenants on u.TenantId equals t.Id
                                                where u.Id.ToString() == userId
                                                select new { t.Slug })
                            .FirstOrDefaultAsync(context.RequestAborted);
                        
                        if (userTenant != null)
                        {
                            // Redirect to tenant-specific NotFound page
                            context.Response.Redirect($"/t/{userTenant.Slug}/NotFound", permanent: false);
                            return;
                        }
                    }
                }
                
                // Fallback: return plain 404
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("Tenant not found.");
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
