using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Crypto;

public sealed class EcJwk
{
    [JsonPropertyName("kty")] public string Kty { get; init; } = "EC";
    [JsonPropertyName("kid")] public string Kid { get; init; } = string.Empty;
    [JsonPropertyName("alg")] public string Alg { get; init; } = "ES256";
    [JsonPropertyName("use")] public string Use { get; init; } = "sig";

    // Public
    [JsonPropertyName("crv")] public string Crv { get; init; } = "P-256";
    [JsonPropertyName("x")] public string X { get; init; } = string.Empty;
    [JsonPropertyName("y")] public string Y { get; init; } = string.Empty;

    // Private
    [JsonPropertyName("d")] public string? D { get; init; }

    public static EcJwk FromECDsa(ECDsa ec, string kid, string alg = "ES256", bool includePrivate = true)
    {
        var parameters = ec.ExportParameters(includePrivate);
        return new EcJwk
        {
            Kid = kid,
            Alg = alg,
            Crv = parameters.Curve.Oid.FriendlyName switch
            {
                "nistP256" or "ECDSA_P256" or "prime256v1" => "P-256",
                "nistP384" or "ECDSA_P384" => "P-384",
                "nistP521" or "ECDSA_P521" => "P-521",
                _ => "P-256"
            },
            X = Base64Url.Encode(parameters.Q.X!),
            Y = Base64Url.Encode(parameters.Q.Y!),
            D = includePrivate && parameters.D != null ? Base64Url.Encode(parameters.D) : null
        };
    }

    public string ToJson(bool includePrivate = true)
    {
        if (includePrivate)
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        }

        var pub = new EcJwk
        {
            Kty = Kty,
            Kid = Kid,
            Alg = Alg,
            Use = Use,
            Crv = Crv,
            X = X,
            Y = Y,
            D = null
        };
        return JsonSerializer.Serialize(pub, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    }
}
