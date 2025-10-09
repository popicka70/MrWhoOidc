using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.Client.Tokens;

internal sealed class MrWhoClientCredentialsManager : IMrWhoClientCredentialsManager
{
    private readonly IMrWhoTokenClient _tokenClient;
    private readonly IOptionsMonitor<MrWhoOidcClientOptions> _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MrWhoClientCredentialsManager> _logger;

    public MrWhoClientCredentialsManager(IMrWhoTokenClient tokenClient, IOptionsMonitor<MrWhoOidcClientOptions> options, IMemoryCache cache, ILogger<MrWhoClientCredentialsManager> logger)
    {
        _tokenClient = tokenClient;
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public async ValueTask<TokenResult> AcquireTokenAsync(string registrationName, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(registrationName))
        {
            throw new ArgumentException("Registration name must be provided.", nameof(registrationName));
        }

        var options = _options.CurrentValue;
        if (!options.ClientCredentials.TryGetValue(registrationName, out var registration) || registration is null)
        {
            throw new InvalidOperationException($"Client credentials registration '{registrationName}' was not found.");
        }

        var resolvedRegistration = registration!;

        var cacheKey = $"m2m:{options.Name}:{registrationName}";
        if (!forceRefresh && _cache.TryGetValue<CachedToken>(cacheKey, out var cached) && cached is not null && !cached.IsExpired)
        {
            _logger.LogDebug("Returning cached client-credentials token for registration {Registration}.", registrationName);
            return cached.Result;
        }

        var request = new ClientCredentialsRequest
        {
            Scopes = resolvedRegistration.Scopes.Count > 0 ? resolvedRegistration.Scopes : null,
            Audience = resolvedRegistration.Audience,
            Resource = resolvedRegistration.Resource
        };

        foreach (var kv in resolvedRegistration.AdditionalParameters)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key))
            {
                request.AdditionalParameters[kv.Key] = kv.Value;
            }
        }

        var result = await _tokenClient.ClientCredentialsAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.IsError)
        {
            _logger.LogWarning("Client-credentials token acquisition failed for registration {Registration}: {Error}", registrationName, result.Error);
            return result;
        }

        var lifetime = ResolveLifetime(resolvedRegistration.CacheLifetime, result.ExpiresIn);
        if (lifetime is not null)
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(lifetime.Value);
            _cache.Set(cacheKey, new CachedToken(result, expiresAt), expiresAt);
        }

        return result;
    }

    private static TimeSpan? ResolveLifetime(TimeSpan? configuredLifetime, long? expiresIn)
    {
        if (configuredLifetime is { } lifetime)
        {
            return lifetime;
        }

        if (expiresIn is long seconds && seconds > 0)
        {
            var ttl = TimeSpan.FromSeconds(seconds);
            return ttl > TimeSpan.FromSeconds(5) ? ttl - TimeSpan.FromSeconds(5) : ttl;
        }

        return null;
    }

    private sealed record CachedToken(TokenResult Result, DateTimeOffset Expires)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= Expires;
    }
}
