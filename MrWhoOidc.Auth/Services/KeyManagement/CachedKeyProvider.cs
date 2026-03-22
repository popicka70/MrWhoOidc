using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.MultiTenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace MrWhoOidc.Auth.Services.KeyManagement;

/// <summary>
/// Implementation of ICachedKeyProvider that maintains an in-memory cache of SecurityKey objects.
/// This avoids repeated deserialization and crypto object instantiation.
/// </summary>
internal sealed class CachedKeyProvider(IServiceScopeFactory scopeFactory, IHttpContextAccessor httpContextAccessor) : ICachedKeyProvider
{
    private readonly ConcurrentDictionary<Guid, (SecurityKey Key, DateTime Expiry)> _activeKeyCache = new();
    private readonly ConcurrentDictionary<Guid, (IReadOnlyCollection<JsonWebKey> Keys, DateTime Expiry)> _jwksCache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<SecurityKey> GetActiveSigningKeyAsync(CancellationToken ct = default)
    {
        // IMPORTANT:
        // Do not create a new DI scope when resolving ITenantAccessor/IKeyStore during a request.
        // TenantResolutionMiddleware populates ITenantAccessor in the request scope.
        // Creating a new scope loses that context and breaks token issuance (/token).
        var requestServices = httpContextAccessor.HttpContext?.RequestServices;
        IServiceScope? scope = null;
        try
        {
            var services = requestServices;
            if (services is null)
            {
                scope = scopeFactory.CreateScope();
                services = scope.ServiceProvider;
            }

            var tenantAccessor = services.GetRequiredService<ITenantAccessor>();
            var keyStore = services.GetRequiredService<IKeyStore>();

            var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

            if (_activeKeyCache.TryGetValue(tenantId, out var cached) && cached.Expiry > DateTime.UtcNow)
            {
                return cached.Key;
            }

            var jwk = await keyStore.GetActiveSigningKeyAsync(ct).ConfigureAwait(false);

            _activeKeyCache[tenantId] = (jwk, DateTime.UtcNow.Add(CacheDuration));
            return jwk;
        }
        finally
        {
            scope?.Dispose();
        }
    }

    public async Task<IReadOnlyCollection<JsonWebKey>> GetPublicJwksAsync(CancellationToken ct = default)
    {
        var requestServices = httpContextAccessor.HttpContext?.RequestServices;
        IServiceScope? scope = null;
        try
        {
            var services = requestServices;
            if (services is null)
            {
                scope = scopeFactory.CreateScope();
                services = scope.ServiceProvider;
            }

            var tenantAccessor = services.GetRequiredService<ITenantAccessor>();
            var keyStore = services.GetRequiredService<IKeyStore>();

            var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

            if (_jwksCache.TryGetValue(tenantId, out var cached) && cached.Expiry > DateTime.UtcNow)
            {
                return cached.Keys;
            }

            var jwks = await keyStore.GetPublicJwksAsync(ct: ct).ConfigureAwait(false);
            var result = jwks.ToList().AsReadOnly();

            _jwksCache[tenantId] = (result, DateTime.UtcNow.Add(CacheDuration));
            return result;
        }
        finally
        {
            scope?.Dispose();
        }
    }

    public void InvalidateCache()
    {
        _activeKeyCache.Clear();
        _jwksCache.Clear();
    }
}
