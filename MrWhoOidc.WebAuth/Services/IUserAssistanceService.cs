using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Settings;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Consent-based user assistance: the requester grants temporary access, the invited helper accepts, and the
/// helper acts as the requester through the overlay principal. This is deliberately separate from platform
/// admin impersonation and tenant support access.
/// </summary>
public interface IUserAssistanceService
{
    /// <summary>ASP.NET session key that points at the helper's durable assistance session.</summary>
    string SessionIdKey { get; }

    /// <summary>Creates a pending request and sends the helper an in-app/email notification.</summary>
    Task<UserAssistanceActionResult> CreateRequestAsync(
        HttpContext context,
        ClaimsPrincipal requester,
        Guid helperUserId,
        UserAssistanceMode mode,
        string reason,
        int? durationMinutes,
        string? ticketReference,
        CancellationToken ct = default);

    /// <summary>Accepts a pending request for the signed-in, explicitly invited helper.</summary>
    Task<UserAssistanceActionResult> AcceptAsync(
        HttpContext context,
        ClaimsPrincipal helper,
        Guid requestId,
        string? token,
        CancellationToken ct = default);

    /// <summary>Declines a pending request for the signed-in, explicitly invited helper.</summary>
    Task<UserAssistanceActionResult> DeclineAsync(
        HttpContext context,
        ClaimsPrincipal helper,
        Guid requestId,
        string? token,
        CancellationToken ct = default);

    /// <summary>The requester cancels their own pending request.</summary>
    Task<UserAssistanceActionResult> CancelAsync(
        HttpContext context,
        ClaimsPrincipal requester,
        Guid requestId,
        CancellationToken ct = default);

    /// <summary>Requester/helper ends, or a tenant manager revokes, an active session.</summary>
    Task<UserAssistanceActionResult> EndAsync(
        HttpContext context,
        ClaimsPrincipal user,
        Guid sessionId,
        string? reason = null,
        CancellationToken ct = default);

    /// <summary>Ends the session referenced by the current helper browser session.</summary>
    Task<UserAssistanceActionResult> StopAsync(HttpContext context, CancellationToken ct = default);

    /// <summary>Helper banner/overlay information for the current request, or null.</summary>
    Task<UserAssistanceInfo?> GetInfoAsync(HttpContext context);

    /// <summary>Requester banner information for the subject's outgoing active session, or null.</summary>
    Task<UserAssistanceInfo?> GetRequesterInfoAsync(HttpContext context);

    /// <summary>Pending request details for the accept page, restricted to the invited helper.</summary>
    Task<UserAssistancePendingInfo?> GetPendingForAcceptAsync(
        ClaimsPrincipal helper,
        Guid requestId,
        string? token,
        CancellationToken ct = default);

    /// <summary>Resolves assistance settings for one tenant.</summary>
    Task<ResolvedAssistanceSettings?> GetSettingsAsync(Guid tenantId);

    /// <summary>Active users in the tenant who may accept at least one assistance mode.</summary>
    Task<IReadOnlyList<UserAssistanceHelperCandidate>> ListEligibleHelpersAsync(
        Guid tenantId,
        Guid requesterUserId,
        CancellationToken ct = default);

    /// <summary>Requests and sessions created by the requester.</summary>
    Task<IReadOnlyList<UserAssistanceInfo>> ListForRequesterAsync(Guid requesterUserId, CancellationToken ct = default);

    /// <summary>Requests and sessions addressed to the helper.</summary>
    Task<IReadOnlyList<UserAssistanceInfo>> ListForHelperAsync(Guid helperUserId, CancellationToken ct = default);

    /// <summary>All tenant requests/sessions visible to a configured manager.</summary>
    Task<IReadOnlyList<UserAssistanceInfo>> ListForManagerAsync(
        ClaimsPrincipal manager,
        Guid tenantId,
        CancellationToken ct = default);

    /// <summary>Builds the requester overlay principal for a valid helper session. Returns null on failure.</summary>
    Task<UserAssistanceOverlay?> ResolveOverlayAsync(
        HttpContext context,
        ClaimsPrincipal actor,
        CancellationToken ct = default);
}

public sealed class UserAssistanceActionResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public Guid? RequestId { get; init; }
    public Guid? SessionId { get; init; }
    public UserAssistanceStatus? Status { get; init; }

    public static UserAssistanceActionResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>Validated overlay data for one request.</summary>
public sealed class UserAssistanceOverlay
{
    public required UserAssistanceSession Session { get; init; }
    public required User RequesterUser { get; init; }
    public required UserAccount RequesterAccount { get; init; }
    public required ClaimsPrincipal Principal { get; init; }
    public required HashSet<Guid> EffectiveRoleIds { get; init; }
}

/// <summary>Display model shared by banners, lists and the acceptance page.</summary>
public sealed class UserAssistanceInfo
{
    public Guid SessionId { get; init; }
    public Guid TenantId { get; init; }
    public Guid RequesterUserId { get; init; }
    public Guid RequesterUserAccountId { get; init; }
    public string RequesterUsername { get; init; } = string.Empty;
    public string? RequesterDisplayName { get; init; }
    public Guid HelperUserId { get; init; }
    public Guid HelperUserAccountId { get; init; }
    public string HelperUsername { get; init; } = string.Empty;
    public string? HelperDisplayName { get; init; }
    public string? HelperEmail { get; init; }
    public UserAssistanceMode Mode { get; init; }
    public UserAssistanceRightsMode RightsMode { get; init; }
    public UserAssistanceStatus Status { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string? TicketReference { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public DateTimeOffset? SessionExpiresAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? EndReason { get; init; }

    public bool IsReadOnly => Mode == UserAssistanceMode.ReadOnly;
    public bool IsPending => Status == UserAssistanceStatus.Pending;
    public bool IsActive => Status == UserAssistanceStatus.Active;
    public bool IsFullTargetRights => RightsMode == UserAssistanceRightsMode.FullTarget;
    public string ModeLabel => IsReadOnly ? "Read-only" : "Read-write";
    public string RightsModeLabel => IsFullTargetRights ? "Full target rights" : "Intersection of roles";
    public DateTimeOffset Deadline => IsPending ? ExpiresAt : SessionExpiresAt ?? ExpiresAt;

    public string RemainingTime
    {
        get
        {
            if (!IsActive) return "Ended";
            var remaining = Deadline - DateTimeOffset.UtcNow;
            if (remaining.TotalSeconds <= 0) return "Expired";
            var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
            return minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";
        }
    }
}

/// <summary>The pending request shown on the helper's accept/decline page.</summary>
public sealed class UserAssistancePendingInfo
{
    public required UserAssistanceSession Session { get; init; }
    public required UserAssistanceInfo Info { get; init; }
}

/// <summary>An active tenant user who can accept assistance in at least one mode.</summary>
public sealed class UserAssistanceHelperCandidate
{
    public Guid UserId { get; init; }
    public Guid UserAccountId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public bool CanReadOnly { get; init; }
    public bool CanReadWrite { get; init; }
}
