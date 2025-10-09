using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Client.Discovery;
using MrWhoOidc.Client.Jwks;
using MrWhoOidc.Client.Options;
using JwtRegisteredClaimNames = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames;

namespace MrWhoOidc.Client.Authorization;

internal sealed class MrWhoAuthorizationManager : IMrWhoAuthorizationManager
{
    private readonly IMrWhoDiscoveryClient _discoveryClient;
    private readonly IOptionsMonitor<MrWhoOidcClientOptions> _options;
    private readonly IMrWhoJwksCache _jwksCache;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MrWhoAuthorizationManager> _logger;
    private readonly JsonWebTokenHandler _jwtHandler = new();

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);

    public MrWhoAuthorizationManager(IMrWhoDiscoveryClient discoveryClient, IOptionsMonitor<MrWhoOidcClientOptions> options, IMrWhoJwksCache jwksCache, IMemoryCache cache, ILogger<MrWhoAuthorizationManager> logger)
    {
        _discoveryClient = discoveryClient;
        _options = options;
        _jwksCache = jwksCache;
        _cache = cache;
        _logger = logger;
    }

    public async ValueTask<AuthorizationRequestContext> BuildAuthorizeRequestAsync(Uri redirectUri, Action<AuthorizationRequestOptions>? configure = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);

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

        var perRequest = new AuthorizationRequestOptions();
        configure?.Invoke(perRequest);

        if (!string.IsNullOrEmpty(perRequest.LoginHint))
        {
            requestParameters["login_hint"] = perRequest.LoginHint;
        }
        if (!string.IsNullOrEmpty(perRequest.Prompt))
        {
            requestParameters["prompt"] = perRequest.Prompt;
        }

        foreach (var kv in perRequest.AdditionalParameters)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key))
            {
                requestParameters[kv.Key] = kv.Value;
            }
        }

        var useJar = perRequest.UseJar ?? opts.Jar.Enabled;
        var useJarm = perRequest.UseJarm ?? opts.Jarm.Enabled;
        var responseMode = perRequest.ResponseMode ?? (useJarm ? opts.Jarm.ResponseMode : null);

        if (!string.IsNullOrEmpty(responseMode))
        {
            requestParameters["response_mode"] = responseMode;
        }

        string? requestObject = null;
        if (useJar)
        {
            requestObject = await CreateRequestObjectAsync(opts, discovery, redirectUri, state, nonce, codeChallenge, responseMode, requestParameters, cancellationToken).ConfigureAwait(false);

            var jarParameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["client_id"] = opts.ClientId,
                ["request"] = requestObject,
                ["state"] = state
            };

            if (!string.IsNullOrEmpty(responseMode))
            {
                jarParameters["response_mode"] = responseMode;
            }

            requestParameters = jarParameters;
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
            CodeVerifier = codeVerifier,
            UsesRequestObject = useJar,
            RequestObject = requestObject
        };
    }

    public async ValueTask<AuthorizationCallbackResult> ValidateCallbackAsync(string state, string? code, string? error, string? response = null, CancellationToken cancellationToken = default)
    {
        if (!_cache.TryGetValue(CacheKey(state), out AuthorizationSession? session))
        {
            return new AuthorizationCallbackResult
            {
                Error = "invalid_state",
                ErrorDescription = "State was not recognized or has expired.",
                State = state
            };
        }

        _cache.Remove(CacheKey(state));
        var storedSession = session;
        if (storedSession is null)
        {
            return new AuthorizationCallbackResult
            {
                Error = "invalid_state",
                ErrorDescription = "State was not recognized or has expired.",
                State = state
            };
        }

        if (!string.IsNullOrEmpty(response))
        {
            return await ValidateJarmAsync(storedSession, response, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(error))
        {
            return new AuthorizationCallbackResult
            {
                Error = error,
                State = state
            };
        }

        if (string.IsNullOrEmpty(code))
        {
            return new AuthorizationCallbackResult
            {
                Error = "invalid_request",
                ErrorDescription = "Missing authorization code in callback.",
                State = state
            };
        }

        return new AuthorizationCallbackResult
        {
            Code = code,
            State = state,
            Nonce = storedSession.Nonce,
            CodeVerifier = storedSession.CodeVerifier
        };
    }

    private async ValueTask<AuthorizationCallbackResult> ValidateJarmAsync(AuthorizationSession session, string responseJwt, CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;

        try
        {
            var jwks = await _jwksCache.GetAsync(cancellationToken).ConfigureAwait(false);
            var validationParameters = CreateJarmValidationParameters(opts, jwks);
            var validationResult = await _jwtHandler.ValidateTokenAsync(responseJwt, validationParameters).ConfigureAwait(false);
            if (!validationResult.IsValid || validationResult.SecurityToken is not JsonWebToken token)
            {
                _logger.LogWarning("JARM response validation failed: {Message}", validationResult.Exception?.Message);
                return InvalidJarm("Failed to validate JARM response.", session.State, responseJwt);
            }

            var stateClaim = GetStringClaim(token, "state");
            if (!string.IsNullOrEmpty(stateClaim) && !string.Equals(stateClaim, session.State, StringComparison.Ordinal))
            {
                _logger.LogWarning("State mismatch detected while validating JARM response (expected {Expected}, received {Actual}).", session.State, stateClaim);
                return InvalidJarm("Returned state claim did not match the original request.", session.State, responseJwt, "invalid_state");
            }

            var error = GetStringClaim(token, "error");
            if (!string.IsNullOrEmpty(error))
            {
                return new AuthorizationCallbackResult
                {
                    Error = error,
                    ErrorDescription = GetStringClaim(token, "error_description"),
                    ErrorUri = GetStringClaim(token, "error_uri"),
                    State = session.State,
                    ResponseJwt = responseJwt,
                    IsJarmResponse = true
                };
            }

            var code = GetStringClaim(token, "code");
            if (string.IsNullOrEmpty(code))
            {
                return InvalidJarm("Authorization code missing from JARM response.", session.State, responseJwt);
            }

            if (opts.Jarm.ValidateHashes)
            {
                var cHash = GetStringClaim(token, "c_hash");
                if (!string.IsNullOrEmpty(cHash))
                {
                    var expected = ComputeLeftHalfHash(code);
                    if (!FixedTimeEquals(cHash, expected))
                    {
                        _logger.LogWarning("c_hash validation failed for JARM response.");
                        return InvalidJarm("c_hash value in JARM response did not validate.", session.State, responseJwt);
                    }
                }

                var sHash = GetStringClaim(token, "s_hash");
                if (!string.IsNullOrEmpty(sHash))
                {
                    var expected = ComputeLeftHalfHash(session.State);
                    if (!FixedTimeEquals(sHash, expected))
                    {
                        _logger.LogWarning("s_hash validation failed for JARM response.");
                        return InvalidJarm("s_hash value in JARM response did not validate.", session.State, responseJwt);
                    }
                }
            }

            return new AuthorizationCallbackResult
            {
                Code = code,
                State = session.State,
                Nonce = session.Nonce,
                CodeVerifier = session.CodeVerifier,
                ResponseJwt = responseJwt,
                IsJarmResponse = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception thrown while validating JARM response");
            return InvalidJarm("Failed to validate JARM response.", session.State, responseJwt);
        }
    }

    private async ValueTask<string> CreateRequestObjectAsync(MrWhoOidcClientOptions opts, MrWhoDiscoveryDocument discovery, Uri redirectUri, string state, string? nonce, string? codeChallenge, string? responseMode, IDictionary<string, string?> requestParameters, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var audience = opts.Jar.Audience ?? opts.Issuer ?? discovery.Issuer;
        if (string.IsNullOrEmpty(audience))
        {
            throw new InvalidOperationException("Unable to determine audience for JAR request object. Configure MrWhoOidcClientOptions.Issuer or Jar.Audience.");
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Iss] = opts.ClientId,
            [JwtRegisteredClaimNames.Aud] = audience,
            [JwtRegisteredClaimNames.Exp] = now.Add(opts.Jar.Lifetime).ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Iat] = now.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Jti] = CreateHandle(),
            ["response_type"] = requestParameters.TryGetValue("response_type", out var responseType) ? responseType : "code",
            ["client_id"] = opts.ClientId,
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = string.Join(' ', opts.Scopes),
            ["state"] = state
        };

        if (!string.IsNullOrEmpty(nonce))
        {
            payload["nonce"] = nonce;
        }

        if (!string.IsNullOrEmpty(opts.Resource))
        {
            payload["resource"] = opts.Resource;
        }
        if (!string.IsNullOrEmpty(opts.Audience))
        {
            payload["audience"] = opts.Audience;
        }
        if (!string.IsNullOrEmpty(codeChallenge))
        {
            payload["code_challenge"] = codeChallenge;
            payload["code_challenge_method"] = "S256";
        }
        if (!string.IsNullOrEmpty(responseMode))
        {
            payload["response_mode"] = responseMode;
        }

        foreach (var kvp in requestParameters)
        {
            if (payload.ContainsKey(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
            {
                continue;
            }
            payload[kvp.Key] = kvp.Value;
        }

        var signingCredentials = await ResolveJarSigningCredentialsAsync(opts, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(opts.Jar.SigningKeyId) && signingCredentials.Key is SecurityKey key)
        {
            key.KeyId = opts.Jar.SigningKeyId;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = opts.ClientId,
            Audience = audience,
            Expires = now.Add(opts.Jar.Lifetime).UtcDateTime,
            Claims = payload,
            SigningCredentials = signingCredentials
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }

    private async ValueTask<SigningCredentials> ResolveJarSigningCredentialsAsync(MrWhoOidcClientOptions opts, CancellationToken cancellationToken)
    {
        if (opts.Jar.SigningCredentialsResolver is not null)
        {
            var creds = await opts.Jar.SigningCredentialsResolver(cancellationToken).ConfigureAwait(false);
            if (creds is not null)
            {
                return creds;
            }
        }

        if (!string.IsNullOrEmpty(opts.ClientSecret))
        {
            var algorithm = string.IsNullOrEmpty(opts.Jar.SigningAlgorithm)
                ? SecurityAlgorithms.HmacSha256
                : opts.Jar.SigningAlgorithm;

            var keyBytes = Encoding.UTF8.GetBytes(opts.ClientSecret);
            var requiredLength = GetMinimumSymmetricKeySizeInBytes(algorithm);

            if (keyBytes.Length < requiredLength)
            {
                keyBytes = DeriveSymmetricKeyMaterial(keyBytes, algorithm, requiredLength);
            }

            var key = new SymmetricSecurityKey(keyBytes)
            {
                KeyId = opts.Jar.SigningKeyId
            };

            return new SigningCredentials(key, algorithm);
        }

        throw new InvalidOperationException("JAR is enabled but no signing credentials are configured. Provide Jar.SigningCredentialsResolver or ClientSecret.");
    }

    private static int GetMinimumSymmetricKeySizeInBytes(string algorithm) => algorithm switch
    {
        SecurityAlgorithms.HmacSha512 => 64,
        SecurityAlgorithms.HmacSha384 => 48,
        SecurityAlgorithms.HmacSha256 => 32,
        SecurityAlgorithms.HmacSha256Signature => 32,
        SecurityAlgorithms.HmacSha384Signature => 48,
        SecurityAlgorithms.HmacSha512Signature => 64,
        _ => 16
    };

    private static byte[] DeriveSymmetricKeyMaterial(byte[] secretBytes, string algorithm, int requiredLength)
    {
        // Derive deterministic key material with a hash sized for the requested HMAC algorithm
        byte[] derived = algorithm switch
        {
            SecurityAlgorithms.HmacSha512 or SecurityAlgorithms.HmacSha512Signature => SHA512.HashData(secretBytes),
            SecurityAlgorithms.HmacSha384 or SecurityAlgorithms.HmacSha384Signature => SHA384.HashData(secretBytes),
            _ => SHA256.HashData(secretBytes)
        };

        if (derived.Length == requiredLength)
        {
            return derived;
        }

        if (derived.Length > requiredLength)
        {
            return derived.AsSpan(0, requiredLength).ToArray();
        }

        var expanded = new byte[requiredLength];
        var offset = 0;
        while (offset < requiredLength)
        {
            var remaining = requiredLength - offset;
            var toCopy = Math.Min(derived.Length, remaining);
            Buffer.BlockCopy(derived, 0, expanded, offset, toCopy);
            offset += toCopy;
        }

        return expanded;
    }

    private TokenValidationParameters CreateJarmValidationParameters(MrWhoOidcClientOptions opts, JsonWebKeySet jwks)
    {
        if (jwks.Keys.Count == 0)
        {
            throw new InvalidOperationException("JWKS document did not contain any keys for JARM validation.");
        }

        return new TokenValidationParameters
        {
            ValidIssuer = opts.Issuer,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidAudience = opts.ClientId,
            RequireSignedTokens = true,
            ValidateLifetime = true,
            ClockSkew = opts.Jarm.ClockSkew,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (token, securityToken, kid, parameters) => jwks.Keys
                .Where(k => string.IsNullOrEmpty(kid) || string.Equals(k.Kid, kid, StringComparison.Ordinal))
                .Select(static k => (SecurityKey)k)
        };
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

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        if (leftBytes.Length != rightBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string ComputeLeftHalfHash(string value)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(value));
        var left = new byte[hash.Length / 2];
        Array.Copy(hash, 0, left, 0, left.Length);
        return Base64UrlEncode(left);
    }

    private static string? GetStringClaim(JsonWebToken token, string claimType)
    {
        if (token.TryGetPayloadValue<string>(claimType, out var stringValue))
        {
            return stringValue;
        }

        if (token.TryGetPayloadValue<object>(claimType, out var objectValue))
        {
            return objectValue?.ToString();
        }

        return null;
    }

    private static AuthorizationCallbackResult InvalidJarm(string description, string state, string response, string error = "invalid_response")
        => new()
        {
            Error = error,
            ErrorDescription = description,
            State = state,
            ResponseJwt = response,
            IsJarmResponse = true
        };

    private sealed record AuthorizationSession(string State, string? Nonce, string? CodeVerifier, DateTimeOffset CreatedAt);
}
