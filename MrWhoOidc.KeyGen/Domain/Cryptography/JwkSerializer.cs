using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.KeyGen.Domain.Cryptography;

/// <summary>
/// Serializes cryptographic keys to JWK/JWKS format.
/// </summary>
public static class JwkSerializer
{
    /// <summary>
    /// Serializes an RSA private key to JWK format.
    /// </summary>
    /// <param name="rsa">RSA key pair.</param>
    /// <param name="kid">Key ID.</param>
    /// <param name="algorithm">Algorithm (RS256, RS384, RS512, PS256).</param>
    /// <returns>JWK JSON string.</returns>
    public static string SerializeRsaPrivateKey(RSA rsa, string kid, string algorithm)
    {
        var securityKey = new RsaSecurityKey(rsa) { KeyId = kid };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(securityKey);
        jwk.Alg = algorithm;
        jwk.Use = "sig";

        return JsonSerializer.Serialize(jwk, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Serializes an RSA public key to JWKS format.
    /// </summary>
    /// <param name="rsa">RSA key pair.</param>
    /// <param name="kid">Key ID.</param>
    /// <param name="algorithm">Algorithm (RS256, RS384, RS512, PS256).</param>
    /// <returns>JWKS JSON string.</returns>
    public static string SerializeRsaPublicKey(RSA rsa, string kid, string algorithm)
    {
        var parameters = rsa.ExportParameters(false);
        var jwk = new
        {
            kty = "RSA",
            use = "sig",
            kid,
            alg = algorithm,
            n = Base64UrlEncoder.Encode(parameters.Modulus!),
            e = Base64UrlEncoder.Encode(parameters.Exponent!)
        };

        var jwks = new { keys = new[] { jwk } };
        return JsonSerializer.Serialize(jwks, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Serializes an ECDSA private key to JWK format.
    /// </summary>
    /// <param name="ecdsa">ECDSA key pair.</param>
    /// <param name="kid">Key ID.</param>
    /// <param name="algorithm">Algorithm (ES256, ES384, ES512).</param>
    /// <returns>JWK JSON string.</returns>
    public static string SerializeEcdsaPrivateKey(ECDsa ecdsa, string kid, string algorithm)
    {
        var securityKey = new ECDsaSecurityKey(ecdsa) { KeyId = kid };
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(securityKey);
        jwk.Alg = algorithm;
        jwk.Use = "sig";

        return JsonSerializer.Serialize(jwk, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Serializes an ECDSA public key to JWKS format.
    /// </summary>
    /// <param name="ecdsa">ECDSA key pair.</param>
    /// <param name="kid">Key ID.</param>
    /// <param name="algorithm">Algorithm (ES256, ES384, ES512).</param>
    /// <param name="curve">Curve name (P-256, P-384, P-521).</param>
    /// <returns>JWKS JSON string.</returns>
    public static string SerializeEcdsaPublicKey(ECDsa ecdsa, string kid, string algorithm, string curve)
    {
        var parameters = ecdsa.ExportParameters(false);
        var crv = curve switch
        {
            "P-256" => "P-256",
            "P-384" => "P-384",
            "P-521" => "P-521",
            _ => throw new ArgumentException("Invalid curve", nameof(curve))
        };

        var jwk = new
        {
            kty = "EC",
            use = "sig",
            kid,
            alg = algorithm,
            crv,
            x = Base64UrlEncoder.Encode(parameters.Q.X!),
            y = Base64UrlEncoder.Encode(parameters.Q.Y!)
        };

        var jwks = new { keys = new[] { jwk } };
        return JsonSerializer.Serialize(jwks, new JsonSerializerOptions { WriteIndented = true });
    }
}
