using MrWhoOidc.Auth.Persistence;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.SupportAccess;

/// <summary>
/// Store service for durable Tenant Support Access sessions.
/// Provides CRUD and lifecycle operations backed by the AuthDbContext.
/// </summary>
public interface ITenantSupportAccessStore
{
    /// <summary>
    /// Retrieves a support access session by its ID, verifying it belongs to the specified tenant.
    /// </summary>
    /// <param name="id">The session ID.</param>
    /// <param name="tenantId">The tenant ID to verify against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The session if found and tenant matches; otherwise, null.</returns>
    Task<TenantSupportAccessSession?> GetByIdAsync(Guid id, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new support access session and persists it.
    /// </summary>
    /// <param name="session">The session to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CreateAsync(TenantSupportAccessSession session, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing support access session with optimistic concurrency control.
    /// The caller must provide the current ConcurrencyToken value; the store verifies it
    /// matches the persisted value before applying the update and generating a new token.
    /// </summary>
    /// <param name="session">The session with updated fields. Must have the current ConcurrencyToken.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(TenantSupportAccessSession session, CancellationToken ct = default);

    /// <summary>
    /// Revokes an active support access session by session ID.
    /// Sets status to Revoked, records revocation metadata, and updates the concurrency token.
    /// </summary>
    /// <param name="sessionId">The session ID to revoke.</param>
    /// <param name="revokerAccountId">The user account ID of the revoking administrator.</param>
    /// <param name="reason">The reason for revocation.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RevokeAsync(Guid sessionId, Guid revokerAccountId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all active (Status == Active and not expired) sessions for a given tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of active sessions for the tenant.</returns>
    Task<List<TenantSupportAccessSession>> GetActiveSessionsAsync(Guid tenantId, CancellationToken ct = default);
}
