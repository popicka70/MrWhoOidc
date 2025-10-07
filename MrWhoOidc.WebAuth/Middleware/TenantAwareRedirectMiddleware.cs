using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Middleware;

/// <summary>
/// Middleware that redirects tenant-unaware URLs to tenant-specific versions for authenticated users.
/// Must be registered after authentication/authorization middleware and after tenant resolution.
/// 
/// Behavior:
/// - If user is authenticated and multi-tenancy is enabled
/// - If the current path does NOT have /t/{slug} prefix
/// - Look up user's tenant and redirect to /t/{slug}{originalPath}
/// - Skip for platform admin routes, static assets, etc.
/// </summary>
public class TenantAwareRedirectMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantAwareRedirectMiddleware> _logger;
    
    public TenantAwareRedirectMiddleware(
        RequestDelegate next,
        ILogger<TenantAwareRedirectMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    
    public async Task InvokeAsync(
        HttpContext context,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions options,
        AuthDbContext dbContext)
    {
        // Skip if multi-tenancy is not enabled
        if (!options.Enabled)
        {
            await _next(context);
            return;
        }
        
        // Skip if user is not authenticated
        if (!context.User?.Identity?.IsAuthenticated ?? true)
        {
            await _next(context);
            return;
        }
        
        var path = context.Request.Path.Value ?? "/";
        
        // Skip if path should not be tenant-aware
        if (ShouldSkipRedirect(path))
        {
            await _next(context);
            return;
        }
        
        // Skip if path already has tenant prefix
        if (path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }
        
        // Get user's tenant
        var userId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Authenticated user has no NameIdentifier claim");
            await _next(context);
            return;
        }
        
        // Look up user's tenant from database
        var user = await (from u in dbContext.Users
                          join t in dbContext.Tenants on u.TenantId equals t.Id
                          where u.Id.ToString() == userId
                          select new { u.TenantId, t.Slug })
            .FirstOrDefaultAsync(context.RequestAborted);
        
        if (user == null)
        {
            _logger.LogWarning("User {UserId} not found in database", userId);
            await _next(context);
            return;
        }
        
        // Redirect to tenant-specific version
        var tenantPath = $"/t/{user.Slug}{path}";
        if (!string.IsNullOrEmpty(context.Request.QueryString.Value))
        {
            tenantPath += context.Request.QueryString.Value;
        }
        
        _logger.LogInformation(
            "Redirecting user {UserId} from tenant-unaware path {OriginalPath} to tenant-specific path {TenantPath}",
            userId, path, tenantPath);
        
        context.Response.Redirect(tenantPath, permanent: false);
    }
    
    /// <summary>
    /// Determines if redirect should be skipped for the given path.
    /// Skips: platform admin routes, auth endpoints that should remain global, static assets, etc.
    /// </summary>
    private static bool ShouldSkipRedirect(string path)
    {
        var lowerPath = path.ToLowerInvariant();
        
        return lowerPath.StartsWith("/health") ||
               lowerPath.StartsWith("/platform-admin") ||
               lowerPath.StartsWith("/platformadmin") ||
               lowerPath.StartsWith("/_") ||
               lowerPath.StartsWith("/swagger") ||
               lowerPath.StartsWith("/api/platform") ||
               lowerPath.StartsWith("/css") ||
               lowerPath.StartsWith("/js") ||
               lowerPath.StartsWith("/lib") ||
               lowerPath.StartsWith("/favicon.ico") ||
               lowerPath.StartsWith("/discovertenant") ||
               lowerPath.StartsWith("/selecttenant") ||
               lowerPath.StartsWith("/switchtenant") ||
               lowerPath.StartsWith("/startimpersonation") ||
               lowerPath.StartsWith("/stopimpersonation");
    }
}

/// <summary>
/// Extension methods for registering tenant-aware redirect middleware.
/// </summary>
public static class TenantAwareRedirectMiddlewareExtensions
{
    /// <summary>
    /// Adds tenant-aware redirect middleware to the pipeline.
    /// Should be called after UseAuthentication() and UseAuthorization().
    /// </summary>
    public static IApplicationBuilder UseTenantAwareRedirect(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TenantAwareRedirectMiddleware>();
    }
}
