using MrWhoOidc.Auth.Protocols;
using System.Collections.Concurrent;

namespace MrWhoOidc.Auth.Services;

public interface IPushedAuthorizationRequestStore
{
    // Creates and stores a PAR entry and returns the opaque request_uri and expiration
    (string requestUri, DateTimeOffset expiresAt) Create(AuthorizeRequest request, string clientId, TimeSpan lifetime);

    // Non-consuming read used during the authorize journey (login/consent redirects may re-hit /authorize)
    PushedAuthorizationRequestEntry? TryGet(string requestUri);

    // Mark the entry as consumed (single-use) once the code is successfully issued
    void MarkConsumed(string requestUri);

    // Back-compat helper: consume immediately
    PushedAuthorizationRequestEntry? TryConsume(string requestUri);
}

public sealed class PushedAuthorizationRequestEntry
{
    public required string ClientId { get; init; }
    public required AuthorizeRequest Request { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class InMemoryPushedAuthorizationRequestStore : IPushedAuthorizationRequestStore
{
    private readonly ConcurrentDictionary<string, (PushedAuthorizationRequestEntry Entry, bool Consumed)> _store = new();

    public (string requestUri, DateTimeOffset expiresAt) Create(AuthorizeRequest request, string clientId, TimeSpan lifetime)
    {
        var id = $"urn:ietf:params:oauth:request_uri:{Guid.NewGuid():N}";
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        var entry = new PushedAuthorizationRequestEntry
        {
            ClientId = clientId,
            Request = request,
            ExpiresAt = expiresAt
        };
        _store[id] = (entry, false);
        return (id, expiresAt);
    }

    public PushedAuthorizationRequestEntry? TryGet(string requestUri)
    {
        if (!_store.TryGetValue(requestUri, out var tuple))
            return null;

        var (entry, consumed) = tuple;
        if (consumed) return null;
        if (DateTimeOffset.UtcNow > entry.ExpiresAt)
        {
            _store.TryRemove(requestUri, out _);
            return null;
        }
        return entry;
    }

    public void MarkConsumed(string requestUri)
    {
        if (_store.TryGetValue(requestUri, out var tuple))
        {
            var (entry, _) = tuple;
            _store[requestUri] = (entry, true);
        }
    }

    public PushedAuthorizationRequestEntry? TryConsume(string requestUri)
    {
        var entry = TryGet(requestUri);
        if (entry is null) return null;
        MarkConsumed(requestUri);
        return entry;
    }
}
