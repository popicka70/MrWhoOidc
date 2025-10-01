using MrWhoOidc.Security;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Validates DPoP proofs for introspection requests.
/// </summary>
public sealed class DPoPValidator(
    IDPoPValidator dpopValidator,
    IDPoPReplayCache replayCache,
    IDPoPNonceStore nonceStore)
{
    public async Task<(bool Valid, IResult? ErrorResult)> ValidateAsync(
        HttpContext http,
        string endpoint,
        string token,
        string expectedJkt)
    {
        var validation = await dpopValidator.ValidateForEndpointAsync(http, endpoint, token).ConfigureAwait(false);

        // Validate nonce
        var clientIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var (nonceOk, serverNonce) = await nonceStore.ValidateOrIssueAsync(
            endpoint,
            clientIp,
            validation.Jkt,
            validation.Nonce
        ).ConfigureAwait(false);

        if (!nonceOk)
        {
            http.Response.Headers["DPoP-Nonce"] = serverNonce;
            return (false, Results.Unauthorized());
        }

        // Validate JKT match
        if (!validation.Ok || 
            string.IsNullOrEmpty(validation.Jkt) || 
            !string.Equals(validation.Jkt, expectedJkt, StringComparison.Ordinal))
        {
            return (false, null);
        }

        // Validate JTI presence
        if (string.IsNullOrEmpty(validation.Jti) || validation.Iat is null)
        {
            return (false, null);
        }

        // Check replay
        var key = $"{validation.Jkt}:{validation.Jti}";
        var expires = DateTimeOffset.FromUnixTimeSeconds(validation.Iat.Value).AddMinutes(5);
        if (!replayCache.TryAdd(key, expires))
        {
            return (false, null);
        }

        return (true, null);
    }
}
