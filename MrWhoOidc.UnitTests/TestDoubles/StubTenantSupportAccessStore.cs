using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.SupportAccess;

namespace MrWhoOidc.UnitTests.TestDoubles;

/// <summary>
/// In-memory stub of <see cref="ITenantSupportAccessStore"/> for unit tests that
/// only need the dependency to be resolvable (no persistence behavior asserted).
/// </summary>
public sealed class StubTenantSupportAccessStore : ITenantSupportAccessStore
{
    private readonly List<TenantSupportAccessSession> _sessions = new();

    public Task<TenantSupportAccessSession?> GetByIdAsync(Guid id, Guid tenantId, CancellationToken ct = default)
        => Task.FromResult(_sessions.FirstOrDefault(s => s.Id == id && s.TenantId == tenantId));

    public Task CreateAsync(TenantSupportAccessSession session, CancellationToken ct = default)
    {
        _sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(TenantSupportAccessSession session, CancellationToken ct = default)
    {
        var existing = _sessions.FirstOrDefault(s => s.Id == session.Id);
        if (existing is not null)
        {
            var idx = _sessions.IndexOf(existing);
            _sessions[idx] = session;
        }
        return Task.CompletedTask;
    }

    public Task RevokeAsync(Guid sessionId, Guid revokerAccountId, string reason, CancellationToken ct = default)
    {
        var existing = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (existing is not null)
        {
            existing.Status = SupportAccessStatus.Revoked;
            existing.RevokedByUserAccountId = revokerAccountId;
            existing.RevocationReason = reason;
            existing.RevokedAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task<List<TenantSupportAccessSession>> GetActiveSessionsAsync(Guid tenantId, CancellationToken ct = default)
        => Task.FromResult(_sessions
            .Where(s => s.TenantId == tenantId && s.Status == SupportAccessStatus.Active && s.ExpiresAt > DateTimeOffset.UtcNow)
            .ToList());
}
