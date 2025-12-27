using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.Auth.Services;

public interface IJarmService
{
    Task<string> CreateSuccessResponseAsync(string clientId, string issuer, string code, string responseMode, string? state);
    Task<string> CreateErrorResponseAsync(string clientId, string issuer, string error, string errorDescription, string? state);
}

public class JarmService(IClientStore clients, IJwtService jwt) : IJarmService
{
    public async Task<string> CreateSuccessResponseAsync(string clientId, string issuer, string code, string responseMode, string? state)
    {
        var enc = await TryGetEncryptingCredentialsAsync(clientId);
        
        var claims = new List<Claim>
        {
            new(OAuthConstants.Parameters.Code, code)
        };
        
        // c_hash per JARM
        var cHash = TokenHashing.ComputeLeftHalfBase64Url(code);
        claims.Add(new(OidcConstants.Claims.CHash, cHash));
        
        if (!string.IsNullOrEmpty(state))
        {
            claims.Add(new(OAuthConstants.Parameters.State, state));
            var sHash = TokenHashing.ComputeLeftHalfBase64Url(state);
            claims.Add(new(OidcConstants.Claims.SHash, sHash));
        }
        
        var exp = DateTimeOffset.UtcNow.AddMinutes(5);
        
        if (enc is not null)
        {
            return await jwt.CreateJwtEncryptedAsync(issuer, clientId, claims, exp, enc).ConfigureAwait(false);
        }
        
        return await jwt.CreateJwtAsync(issuer, clientId, claims, exp).ConfigureAwait(false);
    }

    public async Task<string> CreateErrorResponseAsync(string clientId, string issuer, string error, string errorDescription, string? state)
    {
        var enc = await TryGetEncryptingCredentialsAsync(clientId);

        var claims = new List<Claim>
        {
            new(OAuthConstants.Parameters.Error, error),
            new(OAuthConstants.Parameters.ErrorDescription, errorDescription)
        };
        
        if (!string.IsNullOrEmpty(state))
        {
            claims.Add(new(OAuthConstants.Parameters.State, state));
            var sHash = TokenHashing.ComputeLeftHalfBase64Url(state);
            claims.Add(new(OidcConstants.Claims.SHash, sHash));
        }
        
        var exp = DateTimeOffset.UtcNow.AddMinutes(5);
        
        if (enc is not null)
        {
            return await jwt.CreateJwtEncryptedAsync(issuer, clientId, claims, exp, enc).ConfigureAwait(false);
        }
        
        return await jwt.CreateJwtAsync(issuer, clientId, claims, exp).ConfigureAwait(false);
    }

    private async Task<EncryptingCredentials?> TryGetEncryptingCredentialsAsync(string? clientId)
    {
        try
        {
            if (string.IsNullOrEmpty(clientId)) return null;
            var client = await clients.FindByClientIdAsync(clientId);
            if (client is null) return null;
            var jwks = client.PublicJwksJson;
            if (string.IsNullOrWhiteSpace(jwks)) return null;
            
            var set = new JsonWebKeySet(jwks);
            // Prefer keys with use=enc and RSA
            var key = set.Keys.FirstOrDefault(k => string.Equals(k.Kty, "RSA", StringComparison.OrdinalIgnoreCase) && string.Equals(k.Use, "enc", StringComparison.OrdinalIgnoreCase))
                   ?? set.Keys.FirstOrDefault(k => string.Equals(k.Kty, "RSA", StringComparison.OrdinalIgnoreCase));
            
            if (key is null) return null;
            
            var encCreds = new EncryptingCredentials(key, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256Gcm);
            return encCreds;
        }
        catch
        {
            return null;
        }
    }
}
