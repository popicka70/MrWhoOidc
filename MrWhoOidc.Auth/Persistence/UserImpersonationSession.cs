namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Durable, time-boxed record of one platform administrator acting as one tenant user.
/// The actor and the subject stay distinct: the actor is the authenticated platform administrator, the
/// subject is the user whose identity the OIDC provider assumes for the duration of the session.
/// </summary>
public class UserImpersonationSession
{
    /// <summary>Unique identifier for this impersonation session.</summary>
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>The global account of the platform administrator who started the session.</summary>
    public Guid ActorUserAccountId { get; set; }

    /// <summary>The per-tenant user id of the platform administrator (the auth cookie's NameIdentifier).</summary>
    public Guid ActorUserId { get; set; }

    /// <summary>The actor's username, denormalized for audit and history.</summary>
    public string ActorUsername { get; set; } = string.Empty;

    /// <summary>The tenant the subject belongs to.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The tenant name at the time the session was started.</summary>
    public string TenantName { get; set; } = string.Empty;

    /// <summary>The tenant slug at the time the session was started.</summary>
    public string TenantSlug { get; set; } = string.Empty;

    /// <summary>The per-tenant user the administrator is impersonating.</summary>
    public Guid TargetUserId { get; set; }

    /// <summary>The global account of the impersonated user.</summary>
    public Guid TargetUserAccountId { get; set; }

    /// <summary>The target username, denormalized for audit and history.</summary>
    public string TargetUsername { get; set; } = string.Empty;

    /// <summary>The target display name, denormalized for audit and history.</summary>
    public string? TargetDisplayName { get; set; }

    /// <summary>The target account's security stamp at start; a later rotation ends the session.</summary>
    public string? TargetSecurityStamp { get; set; }

    /// <summary>The OP session created for the target identity.</summary>
    public Guid TargetOpSessionId { get; set; }

    /// <summary>Current lifecycle status.</summary>
    public UserImpersonationStatus Status { get; set; } = UserImpersonationStatus.Active;

    /// <summary>Why the administrator started the session. Required.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Optional ticket or incident reference.</summary>
    public string? TicketReference { get; set; }

    /// <summary>When the session was started.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the session ends at the latest.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the session was stopped or expired.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>When the session was explicitly revoked.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>The account that revoked the session, when it was revoked by an administrator.</summary>
    public Guid? RevokedByUserAccountId { get; set; }

    /// <summary>The revocation reason, when revoked.</summary>
    public string? RevocationReason { get; set; }

    /// <summary>Throttled heartbeat of the session.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Hash of the IP address that started the session (never the address itself).</summary>
    public string? CreatedFromIpHash { get; set; }

    /// <summary>Hash of the User-Agent that started the session.</summary>
    public string? UserAgentHash { get; set; }

    /// <summary>Optimistic concurrency token.</summary>
    public Guid ConcurrencyToken { get; set; } = GuidHelper.NewId();

    /// <summary>The actor's ACR at start, propagated to the target OP session.</summary>
    public string? ActorAcr { get; set; }

    /// <summary>The actor's AMR values at start, space-delimited, propagated to the target OP session.</summary>
    public string? ActorAmrJson { get; set; }

    /// <summary>The actor's upstream IdP at start, if any.</summary>
    public string? ActorIdp { get; set; }
}

/// <summary>Lifecycle status of a user impersonation session.</summary>
public enum UserImpersonationStatus
{
    /// <summary>The session is active and may be used.</summary>
    Active = 0,

    /// <summary>The administrator ended the session.</summary>
    Ended = 1,

    /// <summary>The session reached its expiry.</summary>
    Expired = 2,

    /// <summary>The session was explicitly revoked.</summary>
    Revoked = 3
}
