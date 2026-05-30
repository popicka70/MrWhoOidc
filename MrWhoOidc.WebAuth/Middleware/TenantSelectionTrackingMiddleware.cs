using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Middleware;

/// <summary>
/// Persists the user's current tenant selection into session whenever a tenant-scoped route is accessed.
/// This enables downstream components (e.g., platform admin guards) to understand the user's active tenant
/// even when visiting tenant-unaware routes.
/// </summary>
public class TenantSelectionTrackingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantSelectionTrackingMiddleware> _logger;

    public TenantSelectionTrackingMiddleware(RequestDelegate next, ILogger<TenantSelectionTrackingMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITenantAccessor tenantAccessor, IMultiTenancyOptions options)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Log session cookie status for diagnostics
        var sessionCookieName = "__Host-mrwhooidc-session";
        var hasSessionCookie = context.Request.Cookies.ContainsKey(sessionCookieName);
        var isAuthenticated = context.User?.Identity?.IsAuthenticated ?? false;

        // IMPORTANT: Always track tenant in session when middleware resolves one,
        // even if multi-tenancy is "disabled" by license. This is because:
        // 1. Single-tenant mode still has a default tenant that gets resolved
        // 2. Authorization handlers need tenant context even for platform-admin pages
        // 3. The session must persist tenant between pages that skip tenant resolution
        var tenant = tenantAccessor.CurrentTenant;

        // Always log session state for diagnostic purposes
        string? existingTenantId = null;
        string? existingSlug = null;
        string? sessionId = null;
        bool sessionAvailable = false;

        try
        {
            // Check if session is available - accessing Session may throw if not available
            sessionAvailable = context.Session != null;
            if (sessionAvailable)
            {
                sessionId = context.Session?.Id;
                existingTenantId = context.Session?.GetString(TenantSessionKeys.PreferredTenantId);
                existingSlug = context.Session?.GetString(TenantSessionKeys.PreferredTenantSlug);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TenantTracking] Failed to access session for path {Path}", path);
        }

        _logger.LogWarning("[TenantTracking] Path={Path}, HasSessionCookie={HasCookie}, SessionAvailable={SessionAvailable}, SessionId={SessionId}, CurrentTenant={TenantId}, SessionTenant={SessionTenant}, SessionSlug={SessionSlug}, IsAuthenticated={IsAuthenticated}",
            path, hasSessionCookie, sessionAvailable, sessionId ?? "(none)", tenant?.TenantId.ToString() ?? "(null)", existingTenantId ?? "(null)", existingSlug ?? "(null)", isAuthenticated);

        // Save tenant to session whenever middleware resolves a tenant
        // This ensures session has tenant context for pages that skip tenant resolution (e.g., /platform-admin/*)
        // Previously only saved for /t/ paths, which caused issues when visiting root / or other tenant-resolved paths
        if (tenant != null && sessionAvailable && context.Session != null)
        {
            // Only update session if tenant changed or wasn't set
            if (existingTenantId != tenant.TenantId.ToString())
            {
                _logger.LogWarning("[TenantTracking] STORING tenant {TenantId}/{Slug} in session for path {Path}", tenant.TenantId, tenant.Slug, path);
                context.Session.SetString(TenantSessionKeys.PreferredTenantId, tenant.TenantId.ToString());
                context.Session.SetString(TenantSessionKeys.PreferredTenantSlug, tenant.Slug);

                // Force session to commit immediately
                await context.Session.CommitAsync();
                _logger.LogWarning("[TenantTracking] Session committed for tenant {TenantId}", tenant.TenantId);
            }
        }
        else if (tenant == null && string.IsNullOrEmpty(existingTenantId) && isAuthenticated)
        {
            // IMPORTANT: If no tenant is resolved AND session has no tenant, the user may lose context
            _logger.LogWarning("[TenantTracking] No tenant context for path {Path} and session has no stored tenant (authenticated user) - menu visibility may be affected. HasSessionCookie={HasCookie}, SessionId={SessionId}",
                path, hasSessionCookie, sessionId ?? "(none)");
        }

        await _next(context);
    }
}
