using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Middleware;

/// <summary>
/// Installs the requester's principal for requests made while a consent-based assistance session is active.
/// <para>
/// The helper's authentication cookie stays the durable actor identity. The overlay runs after admin
/// impersonation and before tenant membership and authorization. Control surfaces (assistance inbox/accept,
/// login, logout, MFA, platform administration and impersonation) deliberately keep the helper principal.
/// In read-only mode, unsafe methods are rejected on every overlaid path before endpoint code runs.
/// </para>
/// </summary>
public sealed class UserAssistanceMiddleware(
    RequestDelegate next,
    ILogger<UserAssistanceMiddleware> logger)
{
    public const string OverlayItemKey = "MrWhoOidc.UserAssistanceOverlay";

    /// <summary>The actor principal captured before the requester overlay is installed.</summary>
    public const string ActorPrincipalItemKey = "MrWhoOidc.UserAssistanceActorPrincipal";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantAccessor tenantAccessor,
        IUserAssistanceService assistanceService)
    {
        if (UserImpersonationMiddleware.IsControlPath(context.Request.Path)
            || context.Items.ContainsKey(UserImpersonationMiddleware.OverlayItemKey)
            || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var overlay = await assistanceService.ResolveOverlayAsync(
            context, context.User, context.RequestAborted).ConfigureAwait(false);
        if (overlay is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var tenant = tenantAccessor.CurrentTenant;
        if (tenant is not null && tenant.TenantId != overlay.Session.TenantId)
        {
            logger.LogWarning(
                "Assistance session {SessionId} belongs to tenant {SessionTenant} but the request resolved {RequestTenant}; no overlay",
                overlay.Session.Id, overlay.Session.TenantId, tenant.TenantId);
            await next(context).ConfigureAwait(false);
            return;
        }

        if (overlay.Session.Mode == UserAssistanceMode.ReadOnly
            && !IsSafeMethod(context.Request.Method)
            && !IsReadOnlyExemptPath(context.Request.Path))
        {
            logger.LogWarning(
                "Denied {Method} to {Path} during read-only assistance session {SessionId}",
                context.Request.Method, context.Request.Path, overlay.Session.Id);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                "This assistance session is read-only. The helper cannot perform this action.").ConfigureAwait(false);
            return;
        }

        context.Items[ActorPrincipalItemKey] = context.User;
        context.User = overlay.Principal;
        context.Items[OverlayItemKey] = overlay;
        await next(context).ConfigureAwait(false);
    }

    private static bool IsSafeMethod(string method)
        => HttpMethods.IsGet(method)
           || HttpMethods.IsHead(method)
           || HttpMethods.IsOptions(method);

    /// <summary>
    /// Consent is a write method in the OIDC flow but must remain available in marker-only read-only mode so the
    /// helper can obtain a token that advertises the read-only marker. It is still overlaid.
    /// </summary>
    private static bool IsReadOnlyExemptPath(PathString path)
    {
        var value = StripTenantPrefix(path.Value ?? string.Empty);
        return IsUnder(value, "/consent");
    }

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

public static class UserAssistanceMiddlewareExtensions
{
    /// <summary>Runs after <c>UseUserImpersonation()</c> and before <c>UseTenantMembership()</c>.</summary>
    public static IApplicationBuilder UseUserAssistance(this IApplicationBuilder app)
        => app.UseMiddleware<UserAssistanceMiddleware>();
}
