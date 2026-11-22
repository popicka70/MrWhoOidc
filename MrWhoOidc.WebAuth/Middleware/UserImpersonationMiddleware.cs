using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Middleware;

/// <summary>
/// Installs the target user's principal for requests made while a user impersonation session is active.
/// <para>
/// The administrator's authentication cookie stays the durable actor identity. The overlay runs after the
/// cookie has been validated and before tenant membership and authorization, so every business surface and
/// <c>/authorize</c> see the target user while the administrator's own authority remains out of band.
/// Control surfaces (platform administration, stop, login and logout) deliberately keep the actor principal.
/// </para>
/// </summary>
public sealed class UserImpersonationMiddleware(
    RequestDelegate next,
    ILogger<UserImpersonationMiddleware> logger)
{
    public const string OverlayItemKey = "MrWhoOidc.UserImpersonationOverlay";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantAccessor tenantAccessor,
        IUserImpersonationService impersonationService)
    {
        if (IsControlPath(context.Request.Path)
            || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var sessionId = impersonationService.GetSessionId(context);
        if (sessionId is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Only overlay inside the target tenant. A cross-tenant request without a matching tenant is left to
        // TenantMembershipMiddleware, which treats it as anonymous rather than granting the target access.
        var tenant = tenantAccessor.CurrentTenant;
        if (tenant is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var overlay = await impersonationService.ResolveOverlayAsync(
            context, context.User, context.RequestAborted).ConfigureAwait(false);
        if (overlay is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (overlay.Session.TenantId != tenant.TenantId)
        {
            logger.LogWarning(
                "Impersonation session {SessionId} belongs to tenant {SessionTenant} but the request resolved {RequestTenant}; no overlay",
                overlay.Session.Id, overlay.Session.TenantId, tenant.TenantId);
            await next(context).ConfigureAwait(false);
            return;
        }

        context.User = overlay.Principal;
        context.Items[OverlayItemKey] = overlay;
        await next(context).ConfigureAwait(false);
    }

    internal static bool IsControlPath(PathString path) =>
        path.StartsWithSegments("/platform-admin", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/impersonation", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/login", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/logout", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/account/logout", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/mfa", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/switch-tenant", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/auth/external", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/signin-oidc", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/connect/endsession", StringComparison.OrdinalIgnoreCase);
}

public static class UserImpersonationMiddlewareExtensions
{
    /// <summary>Runs after <c>UseAuthentication()</c> and before <c>UseTenantMembership()</c>.</summary>
    public static IApplicationBuilder UseUserImpersonation(this IApplicationBuilder app)
        => app.UseMiddleware<UserImpersonationMiddleware>();
}
