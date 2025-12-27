using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;
using System.Globalization;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for creating and signing JSON Web Tokens (JWT).
/// </summary>
public interface IJwtService
{
    /// <summary>
    /// Creates a signed JWT.
    /// </summary>
    /// <param name="issuer">The issuer URI.</param>
    /// <param name="audience">The audience.</param>
    /// <param name="claims">The claims to include.</param>
    /// <param name="expires">The expiration time.</param>
    /// <param name="nonce">Optional nonce.</param>
    /// <param name="accessTokenHash">Optional access token hash (at_hash).</param>
    /// <param name="authTime">Optional authentication time (auth_time).</param>
    /// <param name="tokenType">Optional token type (typ header).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A signed JWT string.</returns>
    Task<string> CreateJwtAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default);

    /// <summary>
    /// Creates an encrypted (and signed) JWT.
    /// </summary>
    /// <param name="issuer">The issuer URI.</param>
    /// <param name="audience">The audience.</param>
    /// <param name="claims">The claims to include.</param>
    /// <param name="expires">The expiration time.</param>
    /// <param name="encryptingCredentials">The credentials used for encryption.</param>
    /// <param name="nonce">Optional nonce.</param>
    /// <param name="accessTokenHash">Optional access token hash (at_hash).</param>
    /// <param name="authTime">Optional authentication time (auth_time).</param>
    /// <param name="tokenType">Optional token type (typ header).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An encrypted JWT string.</returns>
    Task<string> CreateJwtEncryptedAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, EncryptingCredentials encryptingCredentials, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default);
}

internal sealed class JwtService(ICachedKeyProvider keyProvider) : IJwtService
{
    public async Task<string> CreateJwtAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
    {
        var list = new List<Claim>(claims);
        if (!string.IsNullOrEmpty(nonce)) list.Add(new Claim("nonce", nonce));
        if (authTime.HasValue) list.Add(new Claim("auth_time", ((DateTimeOffset)authTime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        if (!string.IsNullOrEmpty(accessTokenHash)) list.Add(new Claim("at_hash", accessTokenHash));

        // Required by OIDC: iat (issued at)
        var now = DateTimeOffset.UtcNow;
        list.Add(new Claim(JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(now.UtcDateTime).ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));

        var key = await keyProvider.GetActiveSigningKeyAsync(ct).ConfigureAwait(false);
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: list,
            notBefore: DateTime.UtcNow,
            expires: expires.UtcDateTime,
            signingCredentials: creds
        );

        if (!string.IsNullOrWhiteSpace(tokenType))
        {
            token.Header[JwtHeaderParameterNames.Typ] = tokenType;
        }

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(token);
    }

    public async Task<string> CreateJwtEncryptedAsync(string issuer, string audience, IEnumerable<Claim> claims, DateTimeOffset expires, EncryptingCredentials encryptingCredentials, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
    {
        var list = new List<Claim>(claims);
        if (!string.IsNullOrEmpty(nonce)) list.Add(new Claim("nonce", nonce));
        if (authTime.HasValue) list.Add(new Claim("auth_time", ((DateTimeOffset)authTime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        if (!string.IsNullOrEmpty(accessTokenHash)) list.Add(new Claim("at_hash", accessTokenHash));

        // Required by OIDC: iat (issued at)
        var now = DateTimeOffset.UtcNow;
        list.Add(new Claim(JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(now.UtcDateTime).ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));

        var key = await keyProvider.GetActiveSigningKeyAsync(ct).ConfigureAwait(false);
        var signingCreds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = DateTime.UtcNow,
            Expires = expires.UtcDateTime,
            Claims = list.ToDictionary(c => c.Type, c => (object)c.Value),
            SigningCredentials = signingCreds,
            EncryptingCredentials = encryptingCredentials,
            TokenType = tokenType
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }
}
