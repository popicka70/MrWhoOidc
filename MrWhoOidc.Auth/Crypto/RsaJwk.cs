using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MrWhoOidc.Auth.Crypto;

public static class Base64Url
{
    public static string Encode(byte[] data) => Convert.ToBase64String(data)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    public static string ToBase64(string base64Url)
        => base64Url.Replace('-', '+').Replace('_', '/') + new string('=', (4 - base64Url.Length % 4) % 4);
}

public sealed class RsaJwk
{
    [JsonPropertyName("kty")] public string Kty { get; init; } = "RSA";
    [JsonPropertyName("kid")] public string Kid { get; init; } = string.Empty;
    [JsonPropertyName("alg")] public string Alg { get; init; } = "RS256";
    [JsonPropertyName("use")] public string Use { get; init; } = "sig";

    // Public
    [JsonPropertyName("n")] public string N { get; init; } = string.Empty;
    [JsonPropertyName("e")] public string E { get; init; } = string.Empty;

    // Private (optional, for persistence)
    [JsonPropertyName("d")] public string? D { get; init; }
    [JsonPropertyName("p")] public string? P { get; init; }
    [JsonPropertyName("q")] public string? Q { get; init; }
    [JsonPropertyName("dp")] public string? DP { get; init; }
    [JsonPropertyName("dq")] public string? DQ { get; init; }
    [JsonPropertyName("qi")] public string? QI { get; init; }

    public static RsaJwk FromRSA(RSA rsa, string kid, string alg = "RS256", bool includePrivate = true)
    {
        var p = rsa.ExportParameters(includePrivate);
        return new RsaJwk
        {
            Kid = kid,
            Alg = alg,
            N = Base64Url.Encode(p.Modulus!),
            E = Base64Url.Encode(p.Exponent!),
            D = includePrivate ? Base64Url.Encode(p.D!) : null,
            P = includePrivate ? Base64Url.Encode(p.P!) : null,
            Q = includePrivate ? Base64Url.Encode(p.Q!) : null,
            DP = includePrivate ? Base64Url.Encode(p.DP!) : null,
            DQ = includePrivate ? Base64Url.Encode(p.DQ!) : null,
            QI = includePrivate ? Base64Url.Encode(p.InverseQ!) : null,
        };
    }

    public RSA ToRSA()
    {
        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Convert.FromBase64String(Base64Url.ToBase64(N)),
            Exponent = Convert.FromBase64String(Base64Url.ToBase64(E)),
            D = D != null ? Convert.FromBase64String(Base64Url.ToBase64(D)) : null,
            P = P != null ? Convert.FromBase64String(Base64Url.ToBase64(P)) : null,
            Q = Q != null ? Convert.FromBase64String(Base64Url.ToBase64(Q)) : null,
            DP = DP != null ? Convert.FromBase64String(Base64Url.ToBase64(DP)) : null,
            DQ = DQ != null ? Convert.FromBase64String(Base64Url.ToBase64(DQ)) : null,
            InverseQ = QI != null ? Convert.FromBase64String(Base64Url.ToBase64(QI)) : null,
        });
        return rsa;
    }

    public string ToJson(bool includePrivate = true)
    {
        if (includePrivate)
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        }

        var pub = new RsaJwk
        {
            Kty = Kty,
            Kid = Kid,
            Alg = Alg,
            Use = Use,
            N = N,
            E = E,
            D = null,
            P = null,
            Q = null,
            DP = null,
            DQ = null,
            QI = null
        };
        return JsonSerializer.Serialize(pub, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    }
}
