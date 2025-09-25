using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.Web.Backchannel;

public sealed class LogoutTokenValidator
{
    private readonly ILogger<LogoutTokenValidator> _logger;
    private readonly IHttpClientFactory _http;
    private readonly IJwksCache _jwksCache;
    private readonly IReplayCache _replayCache;
    private readonly BackchannelOptions _options;

    public LogoutTokenValidator(
        ILogger<LogoutTokenValidator> logger,
        IHttpClientFactory http,
        IJwksCache jwksCache,
        IReplayCache replayCache,
        BackchannelOptions options)
    {
        _logger = logger;
        _http = http;
        _jwksCache = jwksCache;
        _replayCache = replayCache;
        _options = options;
    }

    public sealed record Result(bool Success, string? Sid, string? Sub, string? Error);

    public async Task<Result> ValidateAsync(string logoutToken, CancellationToken ct = default)
    {
        try
        {
            if (!_options.Enabled)
                return new Result(false, null, null, "Backchannel disabled");

            if (string.IsNullOrEmpty(_options.Authority) || string.IsNullOrEmpty(_options.ClientId))
                return new Result(false, null, null, "Authority/ClientId not configured");

            // Discover configuration (including JWKS URI) with optional http for dev
            var metadataAddress = _options.Authority.TrimEnd('/') + "/.well-known/openid-configuration";
            var requireHttps = metadataAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = requireHttps });

            var config = await configManager.GetConfigurationAsync(ct);
            var issuer = config.Issuer?.TrimEnd('/') + "/";
            var expectedIssuer = _options.Authority.TrimEnd('/') + "/";
            if (!string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
                return new Result(false, null, null, $"iss mismatch; got '{issuer}', expected '{expectedIssuer}'");

            // Resolve signing keys via JWKS cache
            if (string.IsNullOrEmpty(config.JwksUri))
                return new Result(false, null, null, "No jwks_uri in metadata");
            var jwks = await _jwksCache.GetAsync(config.JwksUri, _options.JwksTtl, _http, ct);
            if (jwks is null || jwks.Keys.Count == 0)
                return new Result(false, null, null, "No signing keys available");

            var tokenHandler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = jwks.Keys,
                ValidIssuer = expectedIssuer,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidAudience = _options.ClientId,
                ValidateLifetime = true,
                ClockSkew = _options.AllowedClockSkew,
                // Additional header check for typ=logout+jwt is done below
            };

            ClaimsPrincipal principal;
            SecurityToken validatedToken;
            try
            {
                principal = tokenHandler.ValidateToken(logoutToken, parameters, out validatedToken);
            }
            catch (SecurityTokenSignatureKeyNotFoundException)
            {
                // Key rollover: refresh JWKS once and retry
                var refreshed = await _jwksCache.GetAsync(config.JwksUri, TimeSpan.Zero, _http, ct);
                if (refreshed is null)
                    throw;
                parameters.IssuerSigningKeys = refreshed.Keys;
                principal = tokenHandler.ValidateToken(logoutToken, parameters, out validatedToken);
            }

            if (validatedToken is not JwtSecurityToken jwt)
                return new Result(false, null, null, "Not a JWT");

            if (!string.Equals(jwt.Header.Typ, "logout+jwt", StringComparison.OrdinalIgnoreCase))
                return new Result(false, null, null, "typ header must be 'logout+jwt'");

            // Required events claim
            var events = principal.FindFirst("events")?.Value;
            if (string.IsNullOrEmpty(events)) return new Result(false, null, null, "missing events claim");
            try
            {
                using var doc = JsonDocument.Parse(events);
                var root = doc.RootElement;
                if (!root.TryGetProperty("http://schemas.openid.net/event/backchannel-logout", out _))
                    return new Result(false, null, null, "events missing backchannel-logout");
            }
            catch (Exception)
            {
                return new Result(false, null, null, "invalid events claim JSON");
            }

            // jti replay
            var jti = principal.FindFirst("jti")?.Value;
            if (string.IsNullOrEmpty(jti)) return new Result(false, null, null, "missing jti");
            if (!await _replayCache.TryStoreAsync(jti, _options.JtiTtl, ct))
                return new Result(false, null, null, "replay detected");

            var sid = principal.FindFirst("sid")?.Value;
            var sub = principal.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(sid) && string.IsNullOrEmpty(sub))
                return new Result(false, null, null, "sid/sub required");

            return new Result(true, sid, sub, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Logout token validation failed");
            return new Result(false, null, null, "exception");
        }
    }
}
