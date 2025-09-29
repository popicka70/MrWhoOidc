using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Discovery;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.Client.Authorization;

internal sealed class MrWhoAuthorizationManager : IMrWhoAuthorizationManager
{
    private readonly IMrWhoDiscoveryClient _discoveryClient;
    private readonly IOptionsMonitor<MrWhoOidcClientOptions> _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MrWhoAuthorizationManager> _logger;

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);

    public MrWhoAuthorizationManager(IMrWhoDiscoveryClient discoveryClient, IOptionsMonitor<MrWhoOidcClientOptions> options, IMemoryCache cache, ILogger<MrWhoAuthorizationManager> logger)
    {
        _discoveryClient = discoveryClient;
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public async ValueTask<AuthorizationRequestContext> BuildAuthorizeRequestAsync(Uri redirectUri, Action<AuthorizationRequestOptions>? configure = null, CancellationToken cancellationToken = default)
    {
        var opts = _options.CurrentValue;
        var discovery = await _discoveryClient.GetAsync(cancellationToken).ConfigureAwait(false);
        var authorizeEndpoint = opts.AuthorizationEndpoint ?? discovery.RequireHttps(discovery.AuthorizationEndpoint, opts.RequireHttpsMetadata);

        var state = CreateHandle();
        string? nonce = null;
        string? codeVerifier = null;
        string? codeChallenge = null;

        if (opts.Scopes.Contains("openid", StringComparer.Ordinal))
        {
            nonce = CreateHandle();
        }

        if (opts.UsePkce)
        {
            codeVerifier = CreateCodeVerifier();
            codeChallenge = CreateCodeChallenge(codeVerifier);
        }

        var requestParameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = opts.ClientId,
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = string.Join(' ', opts.Scopes),
            ["state"] = state
        };

        if (!string.IsNullOrEmpty(nonce))
        {
            requestParameters["nonce"] = nonce;
        }

        if (!string.IsNullOrEmpty(opts.Resource))
        {
            requestParameters["resource"] = opts.Resource;
        }
        if (!string.IsNullOrEmpty(opts.Audience))
        {
            requestParameters["audience"] = opts.Audience;
        }

        if (!string.IsNullOrEmpty(codeChallenge))
        {
            requestParameters["code_challenge"] = codeChallenge;
            requestParameters["code_challenge_method"] = "S256";
        }

        var options = new AuthorizationRequestOptions();
        configure?.Invoke(options);

        if (!string.IsNullOrEmpty(options.LoginHint))
        {
            requestParameters["login_hint"] = options.LoginHint;
        }
        if (!string.IsNullOrEmpty(options.Prompt))
        {
            requestParameters["prompt"] = options.Prompt;
        }

        foreach (var kv in options.AdditionalParameters)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key))
            {
                requestParameters[kv.Key] = kv.Value;
            }
        }

        var query = string.Join('&', requestParameters
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value!)));

        var requestUri = new UriBuilder(authorizeEndpoint)
        {
            Query = query
        }.Uri;

        var session = new AuthorizationSession(state, nonce, codeVerifier, DateTimeOffset.UtcNow);
        _cache.Set(CacheKey(state), session, SessionLifetime);

        _logger.LogDebug("Created authorization request for client {ClientId} with state {State}", opts.ClientId, state);

        return new AuthorizationRequestContext
        {
            RequestUri = requestUri,
            State = state,
            Nonce = nonce,
            CodeVerifier = codeVerifier
        };
    }

    public ValueTask<AuthorizationCallbackResult> ValidateCallbackAsync(string state, string? code, string? error, CancellationToken cancellationToken = default)
    {
        if (!_cache.TryGetValue<AuthorizationSession>(CacheKey(state), out var session))
        {
            return ValueTask.FromResult(new AuthorizationCallbackResult
            {
                Error = "invalid_state",
                ErrorDescription = "State was not recognized or has expired.",
                State = state
            });
        }

        _cache.Remove(CacheKey(state));
        var storedSession = session;

        if (storedSession is null)
        {
            return ValueTask.FromResult(new AuthorizationCallbackResult
            {
                Error = "invalid_state",
                ErrorDescription = "State was not recognized or has expired.",
                State = state
            });
        }

        if (!string.IsNullOrEmpty(error))
        {
            return ValueTask.FromResult(new AuthorizationCallbackResult
            {
                Error = error,
                State = state
            });
        }

        if (string.IsNullOrEmpty(code))
        {
            return ValueTask.FromResult(new AuthorizationCallbackResult
            {
                Error = "invalid_request",
                ErrorDescription = "Missing authorization code in callback.",
                State = state
            });
        }

        return ValueTask.FromResult(new AuthorizationCallbackResult
        {
            Code = code,
            State = state,
            Nonce = storedSession.Nonce,
            CodeVerifier = storedSession.CodeVerifier
        });
    }

    private static string CacheKey(string state) => "mrwho:authsession:" + state;

    private static string CreateHandle()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string verifier)
    {
        using var sha = SHA256.Create();
        var hashed = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hashed);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private sealed record AuthorizationSession(string State, string? Nonce, string? CodeVerifier, DateTimeOffset CreatedAt);
}
