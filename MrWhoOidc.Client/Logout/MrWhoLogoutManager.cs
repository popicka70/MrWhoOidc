using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Client.Discovery;
using MrWhoOidc.Client.Jwks;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.Client.Logout;

internal sealed class MrWhoLogoutManager : IMrWhoLogoutManager
{
    private const string BackchannelEventType = "http://schemas.openid.net/event/backchannel-logout";
    private const string JtiCachePrefix = "mrwho:logout:jti";

    private readonly IMrWhoDiscoveryClient _discoveryClient;
    private readonly IMrWhoJwksCache _jwksCache;
    private readonly IOptionsMonitor<MrWhoOidcClientOptions> _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MrWhoLogoutManager> _logger;

    public MrWhoLogoutManager(IMrWhoDiscoveryClient discoveryClient,
        IMrWhoJwksCache jwksCache,
        IOptionsMonitor<MrWhoOidcClientOptions> options,
        IMemoryCache cache,
        ILogger<MrWhoLogoutManager> logger)
    {
        _discoveryClient = discoveryClient;
        _jwksCache = jwksCache;
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public async ValueTask<FrontChannelLogoutRequest> BuildFrontChannelLogoutAsync(FrontChannelLogoutOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new FrontChannelLogoutOptions();

        var opts = _options.CurrentValue;
        if (!opts.Logout.EnableFrontChannel)
        {
            throw new InvalidOperationException("Front-channel logout is disabled via MrWhoOidcClientOptions.Logout.EnableFrontChannel.");
        }

        var discovery = await _discoveryClient.GetAsync(cancellationToken).ConfigureAwait(false);

        var endSession = discovery.EndSessionEndpoint;
        if (string.IsNullOrWhiteSpace(endSession))
        {
            if (string.IsNullOrWhiteSpace(opts.Issuer))
            {
                throw new InvalidOperationException("Unable to resolve end_session_endpoint. Configure the issuer or ensure discovery exposes the endpoint.");
            }

            endSession = opts.Issuer.TrimEnd('/') + "/logout";
            _logger.LogDebug("end_session_endpoint missing from discovery; using heuristic {Endpoint}", endSession);
        }

        if (!Uri.TryCreate(endSession, UriKind.Absolute, out var endSessionUri))
        {
            throw new InvalidOperationException("The resolved end_session_endpoint is not a valid absolute URI.");
        }

        if (opts.RequireHttpsMetadata && !string.Equals(endSessionUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The end_session_endpoint must use HTTPS when RequireHttpsMetadata is enabled.");
        }

        if (options.PostLogoutRedirectUri is not null && !options.PostLogoutRedirectUri.IsAbsoluteUri)
        {
            throw new ArgumentException("PostLogoutRedirectUri must be an absolute URI when provided.", nameof(options));
        }

        var query = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? state = null;

        if (!options.SuppressState)
        {
            state = string.IsNullOrWhiteSpace(options.State) ? Guid.NewGuid().ToString("N") : options.State;
            query["state"] = state;
        }

        if (options.IncludeClientId)
        {
            query["client_id"] = opts.ClientId;
        }

        if (options.PostLogoutRedirectUri is not null)
        {
            query["post_logout_redirect_uri"] = options.PostLogoutRedirectUri.ToString();
        }

        if (!string.IsNullOrEmpty(options.IdTokenHint))
        {
            query["id_token_hint"] = options.IdTokenHint;
        }

        if (!string.IsNullOrEmpty(options.Sid))
        {
            query["sid"] = options.Sid;
        }

        if (!string.IsNullOrEmpty(options.LogoutHint))
        {
            query["logout_hint"] = options.LogoutHint;
        }

        foreach (var kv in options.AdditionalParameters)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
            {
                continue;
            }

            query[kv.Key] = kv.Value;
        }

        var finalParameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in query)
        {
            if (!string.IsNullOrEmpty(kv.Value))
            {
                finalParameters[kv.Key] = kv.Value!;
            }
        }

        var logoutUrl = QueryHelpers.AddQueryString(endSessionUri.ToString(), finalParameters.ToDictionary(static kv => kv.Key, static kv => (string?)kv.Value, StringComparer.Ordinal));

        return new FrontChannelLogoutRequest
        {
            LogoutUri = new Uri(logoutUrl, UriKind.Absolute),
            State = state,
            HasPostLogoutRedirect = options.PostLogoutRedirectUri is not null,
            Parameters = new ReadOnlyDictionary<string, string>(finalParameters)
        };
    }

    public async ValueTask<BackchannelLogoutValidationResult> ValidateBackchannelLogoutAsync(string logoutToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(logoutToken))
        {
            throw new ArgumentException("A logout_token must be provided.", nameof(logoutToken));
        }

        var opts = _options.CurrentValue;
        if (!opts.Logout.EnableBackchannel)
        {
            _logger.LogDebug("Backchannel logout validation skipped because it is disabled in options.");
            return BackchannelLogoutValidationResult.Disabled("backchannel_disabled");
        }

        if (string.IsNullOrWhiteSpace(opts.Issuer) || string.IsNullOrWhiteSpace(opts.ClientId))
        {
            _logger.LogWarning("Backchannel logout validation failed: issuer or client_id not configured.");
            return new BackchannelLogoutValidationResult { Success = false, Error = "configuration_error" };
        }

        var normalizedIssuer = NormalizeIssuer(opts.Issuer);

        var jwks = await _jwksCache.GetAsync(cancellationToken).ConfigureAwait(false);
        var validationParameters = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = jwks.Keys,
            ValidIssuer = normalizedIssuer,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidAudience = opts.ClientId,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = opts.Logout.BackchannelClockSkew
        };

        ClaimsPrincipal principal;
        SecurityToken validatedToken;
        var handler = new JwtSecurityTokenHandler();

        try
        {
            principal = handler.ValidateToken(logoutToken, validationParameters, out validatedToken);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            _logger.LogInformation("Backchannel logout validation retrying after JWKS refresh.");
            _jwksCache.Invalidate();
            jwks = await _jwksCache.GetAsync(cancellationToken).ConfigureAwait(false);
            validationParameters.IssuerSigningKeys = jwks.Keys;
            principal = handler.ValidateToken(logoutToken, validationParameters, out validatedToken);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            _logger.LogWarning(ex, "Backchannel logout token failed validation");
            return new BackchannelLogoutValidationResult { Success = false, Error = "token_validation_failed" };
        }

        if (validatedToken is not JwtSecurityToken jwt)
        {
            return new BackchannelLogoutValidationResult { Success = false, Error = "invalid_token" };
        }

        if (!string.Equals(jwt.Header.Typ, "logout+jwt", StringComparison.OrdinalIgnoreCase))
        {
            return new BackchannelLogoutValidationResult { Success = false, Error = "invalid_typ" };
        }

        var eventsClaim = principal.FindFirst("events")?.Value;
        if (string.IsNullOrEmpty(eventsClaim) || !ContainsBackchannelEvent(eventsClaim))
        {
            return new BackchannelLogoutValidationResult { Success = false, Error = "missing_events" };
        }

        var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        if (string.IsNullOrEmpty(jti))
        {
            return new BackchannelLogoutValidationResult { Success = false, Error = "missing_jti" };
        }

        var sid = principal.FindFirst("sid")?.Value;
        var sub = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sid) && string.IsNullOrEmpty(sub))
        {
            return new BackchannelLogoutValidationResult { Success = false, Error = "sid_or_sub_required" };
        }

        if (opts.Logout.BackchannelReplayCacheDuration > TimeSpan.Zero)
        {
            var cacheKey = $"{JtiCachePrefix}:{opts.Name}:{jti}";
            if (_cache.TryGetValue(cacheKey, out _))
            {
                return new BackchannelLogoutValidationResult
                {
                    Success = false,
                    Error = "replay_detected",
                    Sid = sid,
                    Subject = sub,
                    JwtId = jti
                };
            }

            _cache.Set(cacheKey, true, opts.Logout.BackchannelReplayCacheDuration);
        }

        var expiresAt = jwt.ValidTo == DateTime.MinValue
            ? (DateTimeOffset?)null
            : new DateTimeOffset(DateTime.SpecifyKind(jwt.ValidTo, DateTimeKind.Utc));

        return new BackchannelLogoutValidationResult
        {
            Success = true,
            Sid = sid,
            Subject = sub,
            JwtId = jti,
            ExpiresAt = expiresAt
        };
    }

    private static bool ContainsBackchannelEvent(string eventsClaim)
    {
        try
        {
            using var doc = JsonDocument.Parse(eventsClaim);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(BackchannelEventType, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeIssuer(string issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return issuer;
        }

        return issuer.EndsWith('/') ? issuer : issuer + "/";
    }
}
