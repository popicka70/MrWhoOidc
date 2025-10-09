using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.Client.Tokens;

internal sealed class MrWhoOnBehalfOfManager : IMrWhoOnBehalfOfManager
{
    private readonly IMrWhoTokenClient _tokenClient;
    private readonly IOptionsMonitor<MrWhoOidcClientOptions> _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MrWhoOnBehalfOfManager> _logger;

    public MrWhoOnBehalfOfManager(IMrWhoTokenClient tokenClient, IOptionsMonitor<MrWhoOidcClientOptions> options, IMemoryCache cache, ILogger<MrWhoOnBehalfOfManager> logger)
    {
        _tokenClient = tokenClient;
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public async ValueTask<TokenResult> AcquireTokenAsync(string registrationName, string subjectAccessToken, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(registrationName))
        {
            throw new ArgumentException("Registration name must be provided.", nameof(registrationName));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(subjectAccessToken);

        var options = _options.CurrentValue;
        if (!options.OnBehalfOf.TryGetValue(registrationName, out var registration) || registration is null)
        {
            throw new InvalidOperationException($"On-behalf-of registration '{registrationName}' was not found.");
        }

        var resolvedRegistration = registration!;

        var cacheKey = BuildCacheKey(options.Name, registrationName, subjectAccessToken);
        if (!forceRefresh && _cache.TryGetValue<CachedToken>(cacheKey, out var cached) && cached is not null && !cached.IsExpired)
        {
            _logger.LogDebug("Returning cached on-behalf-of token for registration {Registration}.", registrationName);
            return cached.Result;
        }

        var request = new TokenExchangeRequest
        {
            SubjectToken = subjectAccessToken,
            SubjectTokenType = resolvedRegistration.SubjectTokenType,
            RequestedTokenType = resolvedRegistration.RequestedTokenType,
            Resource = resolvedRegistration.Resource,
            Audience = resolvedRegistration.Audience,
            Scope = resolvedRegistration.Scope
        };

        foreach (var kv in resolvedRegistration.AdditionalParameters)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key))
            {
                request.AdditionalParameters[kv.Key] = kv.Value;
            }
        }

        var result = await _tokenClient.TokenExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.IsError)
        {
            _logger.LogWarning("On-behalf-of token acquisition failed for registration {Registration}: {Error}", registrationName, result.Error);
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

    private static string BuildCacheKey(string optionsName, string registrationName, string subjectAccessToken)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(subjectAccessToken);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(tokenBytes, hash);
        return $"obo:{optionsName}:{registrationName}:{Convert.ToHexString(hash)}";
    }

    private sealed record CachedToken(TokenResult Result, DateTimeOffset Expires)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= Expires;
    }
}
