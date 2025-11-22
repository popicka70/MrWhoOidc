using Microsoft.AspNetCore.Http;
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

    public TenantSelectionTrackingMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context, ITenantAccessor tenantAccessor, IMultiTenancyOptions options)
    {
        if (options.Enabled)
        {
            var tenant = tenantAccessor.CurrentTenant;
            var path = context.Request.Path.Value ?? string.Empty;

            if (tenant != null && path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
            {
                context.Session.SetString(TenantSessionKeys.PreferredTenantId, tenant.TenantId.ToString());
                context.Session.SetString(TenantSessionKeys.PreferredTenantSlug, tenant.Slug);
            }
        }

        await _next(context);
    }
}
