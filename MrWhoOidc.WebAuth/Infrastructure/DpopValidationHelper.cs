using MrWhoOidc.Security;

namespace MrWhoOidc.WebAuth.Infrastructure;

/// <summary>
/// Centralized DPoP proof validation helper for the /token endpoint (covers standard grants and token-exchange).
/// Keeps handlers focused on flow logic while unifying logging + header handling.
/// </summary>
internal static class DpopValidationHelper
{
    /// <summary>
    /// Validates optional DPoP proof for the token endpoint. Returns (Ok=false) if validation fails.
    /// </summary>
    /// <param name="validator">DPoP validator implementation.</param>
    /// <param name="http">Current HTTP context.</param>
    /// <param name="endpointUrl">Absolute /token endpoint URL.</param>
    /// <param name="athToken">Optional access/subject token used for ATH binding when present (token-exchange).</param>
    /// <param name="logger">Optional logger for warnings.</param>
    /// <returns>(Ok, Jkt)</returns>
    public static async Task<(bool Ok, string? Jkt)> ValidateForTokenEndpointAsync(IDPoPValidator validator, IDPoPReplayCache replayCache, HttpContext http, string endpointUrl, string? athToken, ILogger? logger = null)
    {
        var validation = await validator.ValidateForEndpointAsync(http, endpointUrl, athToken);
        if (!validation.Ok)
        {
            logger?.LogWarning("/token invalid_dpop_proof: reason={Reason} endpoint={Endpoint} method={Method} ip={IP}", 
                validation.Error ?? "unknown", 
                endpointUrl,
                http.Request.Method,
                http.Connection.RemoteIpAddress?.ToString());
            return (false, null);
        }

        // Replay protection: require jti/iat and ensure uniqueness within the iat window.
        if (string.IsNullOrEmpty(validation.Jkt) || string.IsNullOrEmpty(validation.Jti) || validation.Iat is null)
        {
            logger?.LogWarning("/token invalid_dpop_proof: missing jkt/jti/iat endpoint={Endpoint} method={Method} ip={IP}",
                endpointUrl,
                http.Request.Method,
                http.Connection.RemoteIpAddress?.ToString());
            return (false, null);
        }

        var key = $"{validation.Jkt}:{validation.Jti}";
        var expires = DateTimeOffset.FromUnixTimeSeconds(validation.Iat.Value).AddMinutes(5);
        if (!replayCache.TryAdd(key, expires))
        {
            logger?.LogWarning("/token invalid_dpop_proof: replay endpoint={Endpoint} method={Method} ip={IP}",
                endpointUrl,
                http.Request.Method,
                http.Connection.RemoteIpAddress?.ToString());
            return (false, null);
        }

        return (true, validation.Jkt);
    }
}
