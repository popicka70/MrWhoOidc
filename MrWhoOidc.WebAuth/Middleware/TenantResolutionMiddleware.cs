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
        
        // Validate tenant access for authenticated users (after authentication middleware runs)
        // We'll check this after the authentication middleware has run by deferring to after _next
        // Actually, we need to validate BEFORE processing, so check if user is authenticated
        // If user is authenticated, verify they belong to this tenant
        if (context.User?.Identity?.IsAuthenticated ?? false)
        {
            var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(userId))
            {
                // Check if user belongs to this tenant
                var dbContext = context.RequestServices.GetRequiredService<MrWhoOidc.Auth.Persistence.AuthDbContext>();
                var userTenantId = await dbContext.Users
                    .Where(u => u.Id.ToString() == userId)
                    .Select(u => u.TenantId)
                    .FirstOrDefaultAsync(context.RequestAborted);
                
                if (userTenantId != Guid.Empty && userTenantId != tenantContext.TenantId)
                {
                    // User is trying to access a different tenant!
                    _logger.LogWarning(
                        "User {UserId} attempted to access tenant {RequestedTenant} but belongs to different tenant {UserTenant}",
                        userId, tenantContext.TenantId, userTenantId);
                    
                    // Get user's correct tenant slug
                    var correctTenant = await dbContext.Tenants
                        .Where(t => t.Id == userTenantId)
                        .Select(t => t.Slug)
                        .FirstOrDefaultAsync(context.RequestAborted);
                    
                    if (correctTenant != null)
                    {
                        // Redirect to the same path but in user's correct tenant
                        var currentPath = context.Request.Path.Value ?? "/";
                        
                        // Strip the incorrect tenant prefix if present
                        if (currentPath.StartsWith($"/t/{tenantContext.Slug}", StringComparison.OrdinalIgnoreCase))
                        {
                            currentPath = currentPath.Substring($"/t/{tenantContext.Slug}".Length);
                            if (string.IsNullOrEmpty(currentPath))
                            {
                                currentPath = "/";
                            }
                        }
                        
                        var correctPath = options.Enabled 
                            ? $"/t/{correctTenant}{currentPath}"
                            : currentPath;
                        
                        // Preserve query string
                        if (!string.IsNullOrEmpty(context.Request.QueryString.Value))
                        {
                            correctPath += context.Request.QueryString.Value;
                        }
                        
                        _logger.LogInformation(
                            "Redirecting user {UserId} from {WrongPath} to correct tenant path {CorrectPath}",
                            userId, context.Request.Path.Value, correctPath);
                        
                        context.Response.Redirect(correctPath, permanent: false);
                        return;
                    }
                }
            }
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
