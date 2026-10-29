using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages.Account;

/// <summary>
/// The OP sessions (F1) of the signed-in user: when they started, when they were last used, the applications
/// that took part in them, and the deadline that ends them. The user can end a session here; ending a session
/// also ends the refresh tokens it produced.
/// </summary>
[Authorize]
public class SessionsModel(
    AuthDbContext db,
    IUserAgentParser uaParser,
    IOpSessionService opSessions,
    ITenantAccessor tenantAccessor,
    IOptions<OpSessionOptions> sessionOptions,
    ILogger<SessionsModel> logger) : PageModel
{
    public List<SessionViewModel> Sessions { get; private set; } = new();
    public string? Message { get; private set; }

    /// <summary>
    /// The server-side session store is off: the page explains it instead of showing an empty list.
    /// </summary>
    public bool StoreDisabled => sessionOptions.Value.Enabled != true;

    public async Task OnGetAsync()
    {
        if (StoreDisabled)
        {
            Message = "The server-side session store is turned off for this deployment, so there is no session list.";
            return;
        }

        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return;
        }

        var currentSessionId = OpSessionSignIn.CurrentSessionId(User);
        var currentUserAgent = Request.Headers.UserAgent.ToString();
        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return;
        }

        var sessions = await opSessions.ListForUserAsync(tenantId.Value, user.Id, HttpContext.RequestAborted);
        var clientIds = await opSessions.GetClientIdsAsync(
            sessions.Select(s => s.Id).ToArray(),
            HttpContext.RequestAborted);

        Sessions = sessions.Select(s =>
        {
            var uaInfo = uaParser.Parse(s.UserAgent);
            var isCurrentDevice = !string.IsNullOrEmpty(currentUserAgent) &&
                                  !string.IsNullOrEmpty(s.UserAgent) &&
                                  currentUserAgent.Equals(s.UserAgent, StringComparison.OrdinalIgnoreCase);

            return new SessionViewModel
            {
                Id = s.Id,
                AuthTime = s.AuthTime,
                LastSeenAt = s.LastSeenAt,
                ExpiresAt = s.ExpiresAt,
                IdleExpiresAt = s.IdleExpiresAt,
                RevokedAt = s.RevokedAt,
                RevokedReason = s.RevokedReason,
                IpHash = s.IpHash,
                Browser = uaInfo.Browser,
                Os = uaInfo.Os,
                DeviceType = uaInfo.DeviceType,
                DeviceIcon = uaInfo.Icon,
                IsCurrent = currentSessionId is { } cid && cid == s.Id,
                IsCurrentDevice = isCurrentDevice,
                ClientIds = clientIds.TryGetValue(s.Id, out var ids) ? ids : Array.Empty<string>()
            };
        }).ToList();
    }

    /// <summary>
    /// Ends one session of the signed-in user. The current session cannot be ended from here (the page is the
    /// current session); it is ended by signing out.
    /// </summary>
    public async Task<IActionResult> OnPostRevokeAsync(Guid sessionId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/login", new { returnUrl = Url.Page("/account/sessions") });

        if (StoreDisabled)
        {
            Message = "The server-side session store is turned off for this deployment.";
            return RedirectToPage();
        }

        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return RedirectToPage();
        }

        var currentSessionId = OpSessionSignIn.CurrentSessionId(User);
        if (currentSessionId is { } cid && cid == sessionId)
        {
            Message = "The current session cannot be ended here. Sign out to end it.";
            return RedirectToPage();
        }

        // The session must belong to the signed-in user: the id comes from the form, the user from the cookie.
        var session = await opSessions.GetAsync(sessionId, HttpContext.RequestAborted);
        if (session is null || session.UserId != user.Id || session.TenantId != tenantId.Value)
        {
            Message = "Session not found or already ended.";
            return RedirectToPage();
        }

        var revokedTokens = await RevokeAsync(sessionId);
        Message = revokedTokens switch
        {
            null => "The session could not be ended. Try again in a moment.",
            0 => "Session ended.",
            _ => "Session ended. The applications that were signed in with it must sign in again."
        };

        return RedirectToPage();
    }

    /// <summary>
    /// Ends every session of the signed-in user except the current one.
    /// </summary>
    public async Task<IActionResult> OnPostRevokeAllAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/login", new { returnUrl = Url.Page("/account/sessions") });

        if (StoreDisabled)
        {
            Message = "The server-side session store is turned off for this deployment.";
            return RedirectToPage();
        }

        var tenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (tenantId is null)
        {
            return RedirectToPage();
        }

        var revoked = await RevokeAllAsync(tenantId.Value, user.Id);
        Message = revoked is null
            ? "The other sessions could not be ended. Try again in a moment."
            : $"Ended {revoked} other session(s). The current session stays active.";
        return RedirectToPage();
    }

    /// <summary>
    /// Ends a session. Returns the number of revoked refresh tokens, or null when the revocation failed (the
    /// database is down): the page tells the user instead of failing.
    /// </summary>
    private async Task<int?> RevokeAsync(Guid sessionId)
    {
        try
        {
            return await opSessions.RevokeAsync(sessionId, "the user ended the session", revokeTokens: true, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not end the OP session {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>
    /// Ends every session of the user except the current one. Returns the number of ended sessions, or null when
    /// the revocation failed.
    /// </summary>
    private async Task<int?> RevokeAllAsync(Guid tenantId, Guid userId)
    {
        try
        {
            return await opSessions.RevokeAllForUserAsync(
                tenantId,
                userId,
                OpSessionSignIn.CurrentSessionId(User),
                "the user ended the other sessions",
                HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not end the other OP sessions of the user {UserId}", userId);
            return null;
        }
    }

    private async Task<User?> GetCurrentUserAsync()
    {
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId)) return null;

        return await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);
    }

    public class SessionViewModel
    {
        public Guid Id { get; set; }
        public DateTimeOffset AuthTime { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset IdleExpiresAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public string? RevokedReason { get; set; }
        /// <summary>The hash of the address the session was created from (the address itself is not stored).</summary>
        public string? IpHash { get; set; }
        public IReadOnlyList<string> ClientIds { get; set; } = Array.Empty<string>();
        public bool IsCurrent { get; set; }
        public string Browser { get; set; } = "Unknown";
        public string Os { get; set; } = "Unknown";
        public string DeviceType { get; set; } = "desktop";
        public string DeviceIcon { get; set; } = "bi-display";
        public bool IsCurrentDevice { get; set; }
    }
}
