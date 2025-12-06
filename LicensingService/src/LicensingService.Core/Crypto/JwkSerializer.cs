using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace LicensingService.Core.Crypto;

/// <summary>
/// Serializes cryptographic keys to JWK/JWKS format.
/// </summary>
public static class JwkSerializer
{
    /// <summary>
    /// Serializes an ECDSA public key to JWK format (single key).
    /// </summary>
    /// <param name="ecdsa">ECDSA key pair.</param>
    /// <param name="kid">Key ID.</param>
    /// <param name="algorithm">Algorithm (ES256, ES384, ES512).</param>
    /// <returns>JWK JSON string.</returns>
    public static string SerializeEcdsaPublicKeyToJwk(ECDsa ecdsa, string kid, string algorithm = "ES256")
    {
        var parameters = ecdsa.ExportParameters(false);
        var crv = GetCurveName(parameters.Curve);

        var jwk = new Dictionary<string, object>
        {
            ["kty"] = "EC",
            ["use"] = "sig",
            ["kid"] = kid,
            ["alg"] = algorithm,
            ["crv"] = crv,
            ["x"] = Base64UrlEncoder.Encode(parameters.Q.X!),
            ["y"] = Base64UrlEncoder.Encode(parameters.Q.Y!)
        };

        return JsonSerializer.Serialize(jwk, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Serializes multiple ECDSA public keys to JWKS format.
    /// </summary>
    /// <param name="keys">Collection of (ECDsa, kid, algorithm) tuples.</param>
    /// <returns>JWKS JSON string.</returns>
    public static string SerializeToJwks(IEnumerable<(ECDsa Key, string Kid, string Algorithm)> keys)
    {
        var jwkList = new List<object>();

        foreach (var (ecdsa, kid, algorithm) in keys)
        {
            var parameters = ecdsa.ExportParameters(false);
            var crv = GetCurveName(parameters.Curve);

            jwkList.Add(new Dictionary<string, object>
            {
                ["kty"] = "EC",
                ["use"] = "sig",
                ["kid"] = kid,
                ["alg"] = algorithm,
                ["crv"] = crv,
                ["x"] = Base64UrlEncoder.Encode(parameters.Q.X!),
                ["y"] = Base64UrlEncoder.Encode(parameters.Q.Y!)
            });
        }

        var jwks = new { keys = jwkList };
        return JsonSerializer.Serialize(jwks, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string GetCurveName(ECCurve curve)
    {
        if (curve.Oid?.FriendlyName == "nistP256" || curve.Oid?.Value == "1.2.840.10045.3.1.7")
            return "P-256";
        if (curve.Oid?.FriendlyName == "nistP384" || curve.Oid?.Value == "1.3.132.0.34")
            return "P-384";
        if (curve.Oid?.FriendlyName == "nistP521" || curve.Oid?.Value == "1.3.132.0.35")
            return "P-521";

        return "P-256"; // Default fallback
    }
}
