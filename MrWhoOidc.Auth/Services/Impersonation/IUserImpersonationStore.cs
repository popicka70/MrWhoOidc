using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Impersonation;

/// <summary>
/// Store for durable user impersonation sessions. All cross-tenant reads and writes are explicit:
/// platform impersonation is authorized for another tenant and therefore opens a cross-tenant scope.
/// </summary>
public interface IUserImpersonationStore
{
    /// <summary>Loads a session by id across tenants.</summary>
    Task<UserImpersonationSession?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>The active, unexpired session of an actor, if any.</summary>
    Task<UserImpersonationSession?> GetActiveForActorAsync(Guid actorUserAccountId, CancellationToken ct = default);

    /// <summary>
    /// The active, unexpired impersonation session that owns the given target OP session, if any.
    /// </summary>
    Task<UserImpersonationSession?> GetActiveByTargetSessionAsync(Guid targetOpSessionId, CancellationToken ct = default);

    /// <summary>Persists a new session.</summary>
    Task CreateAsync(UserImpersonationSession session, CancellationToken ct = default);

    /// <summary>Persists lifecycle changes to an existing session.</summary>
    Task UpdateAsync(UserImpersonationSession session, CancellationToken ct = default);

    /// <summary>
    /// Ends a session and revokes the target OP session plus the refresh tokens it produced.
    /// Returns false when the row no longer exists or is already inactive.
    /// </summary>
    Task<bool> EndAsync(
        Guid sessionId,
        UserImpersonationStatus status,
        DateTimeOffset endedAt,
        Guid? revokedByUserAccountId,
        string? revocationReason,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes the target OP session and the refresh tokens tied to it. Safe to call repeatedly and across tenants.
    /// </summary>
    Task<int> RevokeTargetSessionAsync(Guid targetOpSessionId, string reason, CancellationToken ct = default);

    /// <summary>Lists sessions for the history page, newest first.</summary>
    Task<List<UserImpersonationSession>> ListAsync(
        string? actorUsername,
        string? targetUsername,
        string? tenantSlug,
        DateTimeOffset? start,
        DateTimeOffset? end,
        int skip,
        int take,
        CancellationToken ct = default);

    /// <summary>Counts sessions matching the history filters.</summary>
    Task<int> CountAsync(
        string? actorUsername,
        string? targetUsername,
        string? tenantSlug,
        DateTimeOffset? start,
        DateTimeOffset? end,
        CancellationToken ct = default);
}
