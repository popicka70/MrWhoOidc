namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Durable record of one consent-based user assistance request and, once accepted, the active session.
/// <para>
/// The requester is the subject who grants access; the helper is the actor who accepts and temporarily
/// assumes the requester's identity. A pending row is a request, not an overlay: the target OP session is
/// created only on acceptance.
/// </para>
/// </summary>
public class UserAssistanceSession
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>The tenant of both the requester and the helper.</summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>The user who granted access (the subject).</summary>
    public Guid RequesterUserId { get; set; }

    /// <summary>The requester's global account id.</summary>
    public Guid RequesterUserAccountId { get; set; }

    /// <summary>The requester's username at request time.</summary>
    public string RequesterUsername { get; set; } = string.Empty;

    /// <summary>The requester's display name at request time.</summary>
    public string? RequesterDisplayName { get; set; }

    /// <summary>The invited helper (the actor).</summary>
    public Guid HelperUserId { get; set; }

    /// <summary>The helper's global account id.</summary>
    public Guid HelperUserAccountId { get; set; }

    /// <summary>The helper's username at request time.</summary>
    public string HelperUsername { get; set; } = string.Empty;

    /// <summary>The helper's email at request time, used for the notification.</summary>
    public string? HelperEmail { get; set; }

    /// <summary>Read-only or read-write assistance.</summary>
    public UserAssistanceMode Mode { get; set; } = UserAssistanceMode.ReadOnly;

    /// <summary>Whether the helper receives all target roles or the requester/helper intersection.</summary>
    public UserAssistanceRightsMode RightsMode { get; set; } = UserAssistanceRightsMode.Intersection;

    /// <summary>Pending, Active, Ended, Expired, Revoked, Declined or Cancelled.</summary>
    public UserAssistanceStatus Status { get; set; } = UserAssistanceStatus.Pending;

    /// <summary>Why the requester asked for help. Required.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Optional ticket or incident reference.</summary>
    public string? TicketReference { get; set; }

    /// <summary>The duration requested by the requester, snapshotted so acceptance honors it.</summary>
    public int RequestedDurationMinutes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When a pending request stops being acceptable.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the helper accepted the request.</summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>When an active session expires.</summary>
    public DateTimeOffset? SessionExpiresAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>The account that ended or revoked the session.</summary>
    public Guid? EndedByUserAccountId { get; set; }

    /// <summary>Optional end/revocation reason.</summary>
    public string? EndReason { get; set; }

    /// <summary>The target OP session created on acceptance.</summary>
    public Guid? TargetOpSessionId { get; set; }

    /// <summary>The requester account's security stamp at acceptance.</summary>
    public string? TargetSecurityStamp { get; set; }

    /// <summary>The helper's ACR at acceptance.</summary>
    public string? ActorAcr { get; set; }

    /// <summary>The helper's space-delimited AMR values at acceptance.</summary>
    public string? ActorAmrJson { get; set; }

    /// <summary>The helper's upstream IdP at acceptance.</summary>
    public string? ActorIdp { get; set; }

    /// <summary>Hash of the one-time invitation token, never the token itself.</summary>
    public string? InvitationTokenHash { get; set; }

    /// <summary>Hash of the IP address that created the request.</summary>
    public string? CreatedFromIpHash { get; set; }

    /// <summary>Hash of the User-Agent that created the request.</summary>
    public string? UserAgentHash { get; set; }

    /// <summary>Throttled heartbeat of an active session.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Optimistic concurrency token.</summary>
    public Guid ConcurrencyToken { get; set; } = GuidHelper.NewId();

    /// <summary>Effective role ids stored for intersection-mode authorization/token filtering.</summary>
    public string? EffectiveRoleIdsJson { get; set; }
}

public enum UserAssistanceStatus
{
    Pending = 0,
    Active = 1,
    Ended = 2,
    Expired = 3,
    Revoked = 4,
    Declined = 5,
    Cancelled = 6
}

public enum UserAssistanceMode
{
    ReadOnly = 0,
    ReadWrite = 1
}

public enum UserAssistanceRightsMode
{
    FullTarget = 0,
    Intersection = 1
}
