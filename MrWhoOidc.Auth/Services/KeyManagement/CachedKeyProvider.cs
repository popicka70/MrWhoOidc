using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace MrWhoOidc.Auth.Services.KeyManagement;

/// <summary>
/// Implementation of ICachedKeyProvider that maintains an in-memory cache of SecurityKey objects.
/// This avoids repeated deserialization and crypto object instantiation.
/// </summary>
internal sealed class CachedKeyProvider(IServiceScopeFactory scopeFactory) : ICachedKeyProvider
{
    private readonly ConcurrentDictionary<Guid, (SecurityKey Key, DateTime Expiry)> _activeKeyCache = new();
    private readonly ConcurrentDictionary<Guid, (IReadOnlyCollection<JsonWebKey> Keys, DateTime Expiry)> _jwksCache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<SecurityKey> GetActiveSigningKeyAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
        var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();

        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        if (_activeKeyCache.TryGetValue(tenantId, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            return cached.Key;
        }

        var jwk = await keyStore.GetActiveSigningKeyAsync(ct).ConfigureAwait(false);
        
        // Convert RsaJwk to SecurityKey
        var microsoftJwk = new JsonWebKey(jwk.ToJson(includePrivate: true));
        
        _activeKeyCache[tenantId] = (microsoftJwk, DateTime.UtcNow.Add(CacheDuration));
        return microsoftJwk;
    }

    public async Task<IReadOnlyCollection<JsonWebKey>> GetPublicJwksAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
        var keyStore = scope.ServiceProvider.GetRequiredService<IKeyStore>();

        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        if (_jwksCache.TryGetValue(tenantId, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            return cached.Keys;
        }

        var jwks = await keyStore.GetPublicJwksAsync(ct).ConfigureAwait(false);
        var result = jwks.Select(k => new JsonWebKey(k.ToJson(includePrivate: false))).ToList().AsReadOnly();

        _jwksCache[tenantId] = (result, DateTime.UtcNow.Add(CacheDuration));
        return result;
    }

    public void InvalidateCache()
    {
        _activeKeyCache.Clear();
        _jwksCache.Clear();
    }
}
