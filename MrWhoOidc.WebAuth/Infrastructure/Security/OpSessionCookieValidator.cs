using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Infrastructure.Security;

/// <summary>
/// F1: the server-side OP session. The cookie carries the session id (the <c>sid</c> of the ID token), and the
/// session decides whether the cookie is still valid: a revoked, expired or idle session signs the user out.
/// <para>
/// A cookie that was issued before the session store (or by a version without it) has no session: one is created
/// lazily and the cookie is re-issued with it, so an upgrade does not force every user to sign in again.
/// </para>
/// </summary>
public static class OpSessionCookieValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        // The security stamp validation runs first and may already have rejected the principal.
        if (context.Principal is null)
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var sessionService = services.GetService<IOpSessionService>();
        var options = services.GetService<IOptions<OpSessionOptions>>();
        if (sessionService is null || options?.Value.Enabled != true)
        {
            return;
        }

        var sidClaim = context.Principal.FindFirst(OidcConstants.Claims.Sid);
        if (sidClaim is not null)
        {
            if (!Guid.TryParse(sidClaim.Value, out var sessionId))
            {
                // A `sid` that is not a session id cannot have been issued by this OP: the cookie is not ours.
                Log(services)?.LogWarning("Rejected a session: the cookie carries a malformed session id");
                await RejectAsync(context).ConfigureAwait(false);
                return;
            }

            var status = await CheckAsync(services, sessionService, sessionId, context).ConfigureAwait(false);
            if (status is null)
            {
                // A transient database error must not sign every user out: the session is simply not checked.
                return;
            }

            switch (status.Value)
            {
                case OpSessionStatus.Valid:
                    return;
                case OpSessionStatus.NotFound:
                    // The session row is gone (it was swept after it ended): the cookie is stale.
                    Log(services)?.LogInformation("Rejected a session: the OP session {SessionId} does not exist", sessionId);
                    await RejectAsync(context).ConfigureAwait(false);
                    return;
                default:
                    Log(services)?.LogInformation("Rejected a session: the OP session {SessionId} is {Status}", sessionId, status);
                    await RejectAsync(context).ConfigureAwait(false);
                    return;
            }
        }

        await CreateSessionAsync(context, sessionService).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the session. Returns null when the check could not be made (a transient database error): the
    /// cookie then stays valid, because a database outage must not sign every user out.
    /// </summary>
    private static async Task<OpSessionStatus?> CheckAsync(
        IServiceProvider services,
        IOpSessionService sessionService,
        Guid sessionId,
        CookieValidatePrincipalContext context)
    {
        try
        {
            return await sessionService.CheckAsync(sessionId, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(services)?.LogWarning(ex, "Could not check the OP session {SessionId}; the session is left intact", sessionId);
            return null;
        }
    }

    /// <summary>
    /// Creates the session for a cookie that has none and re-issues the cookie with its id.
    /// </summary>
    private static async Task CreateSessionAsync(CookieValidatePrincipalContext context, IOpSessionService sessionService)
    {
        var http = context.HttpContext;
        var principal = context.Principal!;
        var services = http.RequestServices;

        var tenantId = services.GetService<ITenantAccessor>()?.CurrentTenant?.TenantId;

        // The session belongs to the per-tenant user (the NameIdentifier), the account is only recorded next to
        // it: in any tenant but the home one the two ids differ.
        var accountIdClaim = principal.FindFirst(UserClaimTypes.UserAccountId);
        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier);
        if (tenantId is null || !Guid.TryParse(userIdClaim?.Value, out var userId))
        {
            // Without a tenant and a user there is nothing to attach a session to; the other checks decide.
            return;
        }

        Guid? accountId = Guid.TryParse(accountIdClaim?.Value, out var parsedAccountId) ? parsedAccountId : null;

        var authTime = DateTimeOffset.UtcNow;
        var authTimeClaim = principal.FindFirst("auth_time")?.Value;
        if (long.TryParse(authTimeClaim, out var authTimeSeconds))
        {
            authTime = DateTimeOffset.FromUnixTimeSeconds(authTimeSeconds);
        }

        var acr = principal.FindFirst(OidcConstants.Claims.Acr)?.Value;
        var amr = principal.Claims
            .Where(c => c.Type == OidcConstants.Claims.Amr)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var idp = principal.FindFirst(OidcConstants.Claims.Idp)?.Value;
        var audit = services.GetService<IAuditSink>();
        var ipHash = audit?.HashValue(http.Connection.RemoteIpAddress?.ToString());
        var userAgent = principal.FindFirst("User-Agent")?.Value ?? http.Request.Headers.UserAgent.ToString();

        OpSession session;
        try
        {
            session = await sessionService.CreateAsync(
                tenantId.Value,
                userId,
                userAccountId: accountId,
                acr: acr,
                amrJson: amr.Length > 0 ? string.Join(' ', amr) : null,
                upstreamIdp: idp,
                ipHash: ipHash,
                userAgent: string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
                authTime: authTime,
                ct: http.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A session that cannot be created must not sign the user out; the cookie stays as it is.
            Log(services)?.LogWarning(ex, "Could not create the OP session; the session stays without one");
            return;
        }

        var claims = principal.Claims.Where(c => c.Type != OidcConstants.Claims.Sid).ToList();
        claims.Add(new Claim(OidcConstants.Claims.Sid, session.Id.ToString("N")));
        var identity = new ClaimsIdentity(claims, principal.Identity?.AuthenticationType ?? CookieAuthenticationDefaults.AuthenticationScheme);
        var newPrincipal = new ClaimsPrincipal(identity);

        context.ReplacePrincipal(newPrincipal);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, newPrincipal, context.Properties).ConfigureAwait(false);
    }

    private static ILogger? Log(IServiceProvider services) =>
        services.GetService<ILoggerFactory>()?.CreateLogger("OpSessionCookieValidator");

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    }
}
