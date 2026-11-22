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

    /// <summary>
    /// The actor principal captured before the target overlay is installed. Layout-level forms that post to a
    /// control endpoint (for example the banner's stop form) must mint their antiforgery token for the actor:
    /// the endpoint is processed without the overlay, so a token bound to the target would be rejected.
    /// </summary>
    public const string ActorPrincipalItemKey = "MrWhoOidc.UserImpersonationActorPrincipal";

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

        context.Items[ActorPrincipalItemKey] = context.User;
        context.User = overlay.Principal;
        context.Items[OverlayItemKey] = overlay;
        await next(context).ConfigureAwait(false);
    }

    internal static bool IsControlPath(PathString path)
    {
        var value = StripTenantPrefix(path.Value ?? string.Empty);
        return IsUnder(value, "/platform-admin")
               || IsUnder(value, "/impersonation")
               || IsUnder(value, "/assistance")
               || IsUnder(value, "/account/assistance")
               || IsUnder(value, "/login")
               || IsUnder(value, "/logintotp")
               || IsUnder(value, "/logout")
               || IsUnder(value, "/account/logout")
               || IsUnder(value, "/mfa")
               || IsUnder(value, "/switch-tenant")
               || IsUnder(value, "/select-tenant")
               || IsUnder(value, "/discover-tenant")
               || IsUnder(value, "/auth/external")
               || IsUnder(value, "/signin-oidc")
               || IsUnder(value, "/connect/endsession");
    }

    /// <summary>
    /// A tenant-prefixed route such as /t/default/login must be treated like /login: the overlay must not
    /// install the target principal on login, logout, MFA or stop control surfaces.
    /// </summary>
    private static string StripTenantPrefix(string value)
    {
        if (!value.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var secondSlash = value.IndexOf('/', 3);
        return secondSlash >= 0 ? value[secondSlash..] : "/";
    }

    private static bool IsUnder(string value, string prefix)
        => value.Equals(prefix, StringComparison.OrdinalIgnoreCase)
           || value.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
}

public static class UserImpersonationMiddlewareExtensions
{
    /// <summary>Runs after <c>UseAuthentication()</c> and before <c>UseTenantMembership()</c>.</summary>
    public static IApplicationBuilder UseUserImpersonation(this IApplicationBuilder app)
        => app.UseMiddleware<UserImpersonationMiddleware>();
}
