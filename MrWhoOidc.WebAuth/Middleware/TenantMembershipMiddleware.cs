using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.SupportAccess;

namespace MrWhoOidc.WebAuth.Middleware;

/// <summary>
/// Keeps a session from one tenant out of another. Auth cookies are scoped to "/", so a cookie issued for
/// tenant A also arrives on /t/B/... routes. When the authenticated user belongs to a different tenant than the
/// one resolved for the request, the request continues as anonymous: B's pages send the user to B's login and
/// B's /authorize prompts for B instead of issuing codes for A's user.
/// The only exception is an active support-access session for this tenant held by the same platform admin.
/// Must run after UseAuthentication() and before UseAuthorization().
/// </summary>
public sealed class TenantMembershipMiddleware(RequestDelegate next, ILogger<TenantMembershipMiddleware> logger)
{
    internal const string SupportAccessSessionKey = "SupportAccessSessionId";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantAccessor tenantAccessor,
        AuthDbContext db,
        ITenantSupportAccessStore supportAccessStore)
    {
        var tenant = tenantAccessor.CurrentTenant;
        if (tenant is not null
            && context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var userId))
        {
            // IgnoreQueryFilters: the tenant filter is already scoped to the requested tenant, so a filtered
            // lookup could never find a user from another tenant.
            var userTenantId = await db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => (Guid?)u.TenantId)
                .FirstOrDefaultAsync(context.RequestAborted);

            if (userTenantId is { } owner && owner != tenant.TenantId
                && !await HasSupportAccessAsync(context, supportAccessStore, userId, tenant.TenantId))
            {
                logger.LogWarning(
                    "SECURITY: session of user {UserId} from tenant {UserTenant} used on tenant {RequestedTenant}; treating request as anonymous",
                    userId, owner, tenant.TenantId);
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
        }

        await next(context);
    }

    private static async Task<bool> HasSupportAccessAsync(HttpContext context, ITenantSupportAccessStore store, Guid userId, Guid tenantId)
    {
        var raw = context.Session.IsAvailable ? context.Session.GetString(SupportAccessSessionKey) : null;
        if (!Guid.TryParse(raw, out var sessionId))
        {
            return false;
        }

        // Tenant-admin authorization re-verifies the platform-admin role; this only keeps the identity.
        var session = await store.GetByIdAsync(sessionId, tenantId, context.RequestAborted);
        return session is { Status: SupportAccessStatus.Active }
               && session.ExpiresAt > DateTimeOffset.UtcNow
               && session.PlatformAdminUserAccountId == userId;
    }
}

public static class TenantMembershipMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantMembership(this IApplicationBuilder app)
        => app.UseMiddleware<TenantMembershipMiddleware>();
}
