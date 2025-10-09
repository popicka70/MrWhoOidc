using MrWhoOidc.Auth.Protocols;
using System.Collections.Concurrent;
using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.Auth.Services;

public interface IPushedAuthorizationRequestStore
{
    // Persist a PAR entry using caller-provided opaque id. Optionally persist the absolute request_uri for auditing.
    DateTimeOffset Create(string id, AuthorizeRequest request, string clientId, TimeSpan lifetime, string? requestUri);
    // Non-consuming read by id
    PushedAuthorizationRequestEntry? TryGetById(string id);
    // Mark consumed by id
    void MarkConsumedById(string id);
    // Convenience helper
    PushedAuthorizationRequestEntry? TryConsumeById(string id);
}

public sealed class PushedAuthorizationRequestEntry
{
    public required string ClientId { get; init; }
    public required AuthorizeRequest Request { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class EfPushedAuthorizationRequestStore(AuthDbContext db, IOptions<AuthOptions> authOptions, ITenantAccessor tenantAccessor) : IPushedAuthorizationRequestStore
{
    public DateTimeOffset Create(string id, AuthorizeRequest request, string clientId, TimeSpan lifetime, string? requestUri)
    {
        if (!TryToGuid(id, out var gid)) throw new ArgumentException("Invalid id format", nameof(id));

        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        // Enforce per-client pending limit (not yet consumed and not expired)
        var now = DateTimeOffset.UtcNow;
        var limit = Math.Max(1, authOptions.Value.ParClientPendingLimit);
        var pending = db.PushedAuthorizationRequests.AsNoTracking()
            .Count(e => e.TenantId == tenantId && e.ClientId == clientId && !e.Consumed && e.ExpiresAt > now);
        if (pending >= limit)
        {
            throw new InvalidOperationException("PAR pending limit reached");
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        var entity = new PushedAuthorizationRequest
        {
            Id = gid,
            TenantId = tenantId,
            RequestUri = requestUri ?? string.Empty,
            ClientId = clientId,
            RequestJson = JsonSerializer.Serialize(request),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            Consumed = false
        };
        db.PushedAuthorizationRequests.Add(entity);
        db.SaveChanges();
        return expiresAt;
    }

    public PushedAuthorizationRequestEntry? TryGetById(string id)
    {
        if (!TryToGuid(id, out var gid)) return null;
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        var now = DateTimeOffset.UtcNow;
        var entity = db.PushedAuthorizationRequests.AsNoTracking()
            .FirstOrDefault(e => e.Id == gid && e.TenantId == tenantId);
        if (entity is null || entity.Consumed || entity.ExpiresAt < now)
        {
            if (entity is { ExpiresAt: var exp } && exp < now)
            {
                // Opportunistic cleanup of expired rows for this tenant
                var expired = db.PushedAuthorizationRequests
                    .Where(e => e.TenantId == tenantId && e.ExpiresAt < now).ToList();
                if (expired.Count > 0)
                {
                    db.PushedAuthorizationRequests.RemoveRange(expired);
                    db.SaveChanges();
                }
            }
            return null;
        }

        var req = JsonSerializer.Deserialize<AuthorizeRequest>(entity.RequestJson) ?? new AuthorizeRequest();
        return new PushedAuthorizationRequestEntry { ClientId = entity.ClientId, Request = req, ExpiresAt = entity.ExpiresAt };
    }

    public void MarkConsumedById(string id)
    {
        if (!TryToGuid(id, out var gid)) return;
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        var entity = db.PushedAuthorizationRequests
            .FirstOrDefault(e => e.Id == gid && e.TenantId == tenantId);
        if (entity is null) return;
        if (!entity.Consumed)
        {
            entity.Consumed = true;
            db.SaveChanges();
        }
    }

    public PushedAuthorizationRequestEntry? TryConsumeById(string id)
    {
        var entry = TryGetById(id);
        if (entry is null) return null;
        MarkConsumedById(id);
        return entry;
    }

    private static bool TryToGuid(string id, out Guid gid)
    {
        // Guid formats
        if (Guid.TryParse(id, out gid)) return true;

        // base64url 128-bit (unpadded). Common lengths: 22 (unpadded), 24 (padded removed handling below)
        try
        {
            var s = id.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            var bytes = Convert.FromBase64String(s);
            if (bytes.Length == 16)
            {
                gid = new Guid(bytes);
                return true;
            }
        }
        catch
        {
            // ignored
        }
        gid = default;
        return false;
    }
}

// Optional in-memory implementation for tests/future swap
internal sealed class InMemoryPushedAuthorizationRequestStore : IPushedAuthorizationRequestStore
{
    private readonly ConcurrentDictionary<string, (PushedAuthorizationRequestEntry Entry, bool Consumed, DateTimeOffset ExpiresAt)> _store = new();

    public DateTimeOffset Create(string id, AuthorizeRequest request, string clientId, TimeSpan lifetime, string? requestUri)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        var entry = new PushedAuthorizationRequestEntry
        {
            ClientId = clientId,
            Request = request,
            ExpiresAt = expiresAt
        };
        _store[id] = (entry, false, expiresAt);
        return expiresAt;
    }

    public PushedAuthorizationRequestEntry? TryGetById(string id)
    {
        if (!_store.TryGetValue(id, out var tuple))
            return null;
        var (entry, consumed, expiresAt) = tuple;
        if (consumed) return null;
        if (DateTimeOffset.UtcNow > expiresAt)
        {
            _store.TryRemove(id, out _);
            return null;
        }
        return entry;
    }

    public void MarkConsumedById(string id)
    {
        if (_store.TryGetValue(id, out var tuple))
        {
            var (entry, _, expiresAt) = tuple;
            _store[id] = (entry, true, expiresAt);
        }
    }

    public PushedAuthorizationRequestEntry? TryConsumeById(string id)
    {
        var entry = TryGetById(id);
        if (entry is null) return null;
        MarkConsumedById(id);
        return entry;
    }
}
