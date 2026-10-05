using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Middleware;

/// <summary>
/// D17: the EF tenant query filter fails closed, so a query issued without a tenant sees no tenant-scoped rows.
/// The platform administration surfaces (<c>/platform-admin/**</c>, including its APIs, and the platform-admin-only
/// <c>/health/**</c> diagnostics) are deliberately cross-tenant and are skipped by tenant resolution. For those paths,
/// and only while no tenant is set after authentication/authorization, the rest of the request runs inside an
/// explicit <see cref="TenantFilterScope.BeginSystemScope"/>. A request that carries a tenant (e.g. a bearer token
/// that resolved the platform tenant) keeps its tenant filter.
/// </summary>
public sealed class PlatformSystemScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantAccessor tenantAccessor)
    {
        if (tenantAccessor.CurrentTenant is null && IsPlatformPath(context.Request.Path))
        {
            using (TenantFilterScope.BeginSystemScope())
            {
                await next(context).ConfigureAwait(false);
            }

            return;
        }

        await next(context).ConfigureAwait(false);
    }

    internal static bool IsPlatformPath(PathString path) =>
        path.StartsWithSegments("/platform-admin", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
}
