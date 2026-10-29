using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Starts the server-side OP session (F1) of a sign-in and puts its id into the cookie as the <c>sid</c> claim.
/// <para>
/// Every path that issues the sign-in cookie goes through here, so the session and the cookie cannot drift apart:
/// the session row is written before the cookie, and the <c>sid</c> is the unhyphenated GUID ("N" format) that
/// the compatibility path (<see cref="Infrastructure.Security.OpSessionCookieValidator"/>) uses too.
/// </para>
/// <para>
/// The session is a convenience, not a lock: when it cannot be created (the store is off, no tenant, the database
/// is down), the sign-in still succeeds and the cookie keeps its current behaviour. The lazy compatibility path
/// then attaches a session to the cookie on the next request.
/// </para>
/// </summary>
public static class OpSessionSignIn
{
    /// <summary>
    /// Creates the session of a sign-in and adds its <c>sid</c> to <paramref name="claims"/>.
    /// </summary>
    /// <param name="http">The request that carries the sign-in.</param>
    /// <param name="claims">The claim set of the cookie; the <c>sid</c> is appended to it.</param>
    /// <param name="userId">The per-tenant user the session belongs to.</param>
    /// <param name="userAccountId">The global account of the user, when it is known.</param>
    /// <param name="authTime">When the user authenticated (the <c>auth_time</c> of the cookie).</param>
    /// <param name="tenantId">The tenant the session belongs to; the current tenant when null.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The id of the new session, or null when no session was created.</returns>
    public static async Task<Guid?> StartAsync(
        HttpContext http,
        List<Claim> claims,
        Guid userId,
        Guid? userAccountId,
        DateTimeOffset authTime,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(claims);

        var services = http.RequestServices;
        var sessionService = services.GetService<IOpSessionService>();
        var options = services.GetService<IOptions<OpSessionOptions>>();
        if (sessionService is null || options?.Value.Enabled != true)
        {
            return null;
        }

        var effectiveTenantId = tenantId ?? services.GetService<ITenantAccessor>()?.CurrentTenant?.TenantId;
        if (effectiveTenantId is null)
        {
            // Without a tenant there is nothing to attach the session to; the cookie keeps its current behaviour.
            return null;
        }

        var acr = FindFirst(claims, OidcConstants.Claims.Acr);
        var amr = claims
            .Where(c => c.Type == OidcConstants.Claims.Amr)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var idp = FindFirst(claims, OidcConstants.Claims.Idp);
        var audit = services.GetService<IAuditSink>();
        var ipHash = audit?.HashValue(http.Connection.RemoteIpAddress?.ToString());
        var userAgent = http.Request.Headers.UserAgent.ToString();

        OpSession session;
        try
        {
            session = await sessionService.CreateAsync(
                effectiveTenantId.Value,
                userId,
                userAccountId: userAccountId,
                acr: acr,
                amrJson: amr.Length > 0 ? string.Join(' ', amr) : null,
                upstreamIdp: idp,
                ipHash: ipHash,
                userAgent: string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
                authTime: authTime,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A session that cannot be created must not fail the sign-in: the cookie keeps its current behaviour
            // and the compatibility path attaches a session later.
            Log(services)?.LogWarning(ex, "Could not create the OP session for the sign-in; the cookie stays without one");
            return null;
        }

        claims.Add(new Claim(OidcConstants.Claims.Sid, session.Id.ToString("N")));
        return session.Id;
    }

    /// <summary>
    /// The <c>sid</c> of the current cookie, when it carries one.
    /// </summary>
    public static Guid? CurrentSessionId(ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirst(OidcConstants.Claims.Sid)?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private static string? FindFirst(List<Claim> claims, string type)
    {
        foreach (var claim in claims)
        {
            if (claim.Type == type && !string.IsNullOrWhiteSpace(claim.Value))
            {
                return claim.Value;
            }
        }

        return null;
    }

    private static ILogger? Log(IServiceProvider services) =>
        services.GetService<ILoggerFactory>()?.CreateLogger("OpSessionSignIn");
}
