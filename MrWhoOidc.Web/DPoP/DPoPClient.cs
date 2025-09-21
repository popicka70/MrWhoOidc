using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.Web.DPoP;

public sealed class DPoPKeyStore
{
    // Make the key process-wide so multiple DI containers (e.g., OIDC backchannel vs. app) share the same key
    private static ECDsa? _ecdsa;
    private static JsonWebKey? _jwk;

    public (ECDsa PrivateKey, JsonWebKey PublicJwk) GetOrCreateKey()
    {
        if (_ecdsa is not null && _jwk is not null) return (_ecdsa, _jwk);
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        var x = Base64UrlEncoder.Encode(parameters.Q.X!);
        var y = Base64UrlEncoder.Encode(parameters.Q.Y!);
        var jwk = new JsonWebKey($"{{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"{x}\",\"y\":\"{y}\"}}");
        _ecdsa = ecdsa;
        _jwk = jwk;
        return (ecdsa, jwk);
    }
}

public static class DPoPProof
{
    public static string Create(ECDsa key, JsonWebKey jwk, string method, string absoluteUrl, string? ath = null, string? nonce = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var jti = Guid.NewGuid().ToString();

        var header = new JwtHeader(new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256));
        header[JwtHeaderParameterNames.Typ] = "dpop+jwt";

        // Set 'jwk' as a plain object with public members only to ensure serializability
        var jwkHeader = new Dictionary<string, object?>
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = jwk.X,
            ["y"] = jwk.Y
        };
        header["jwk"] = jwkHeader;

        var payload = new JwtPayload
        {
            { "htm", method },
            { "htu", absoluteUrl },
            { "iat", now },
            { "jti", jti }
        };
        if (!string.IsNullOrEmpty(ath)) payload["ath"] = ath;
        if (!string.IsNullOrEmpty(nonce)) payload["nonce"] = nonce;

        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string ComputeAth(string accessToken)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
        return Base64UrlEncoder.Encode(hash);
    }
}
