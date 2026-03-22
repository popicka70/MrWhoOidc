using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.UnitTests.TestSupport;

/// <summary>
/// Provides shared cryptographic keys for unit tests to avoid the overhead
/// of generating new RSA/ECDSA keys for each test method.
/// 
/// RSA 2048-bit key generation takes ~20-50ms per call. By sharing keys,
/// we can reduce test suite execution time significantly.
/// </summary>
public static class SharedTestKeys
{
    // Lazy initialization ensures keys are only generated if needed
    private static readonly Lazy<RSA> s_rsa2048 = new(() => RSA.Create(2048), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<RSA> s_rsa2048Alt = new(() => RSA.Create(2048), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<RSA> s_rsa4096 = new(() => RSA.Create(4096), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ECDsa> s_ecdsaP256 = new(() => ECDsa.Create(ECCurve.NamedCurves.nistP256), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ECDsa> s_ecdsaP384 = new(() => ECDsa.Create(ECCurve.NamedCurves.nistP384), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Shared RSA 2048-bit key for general test use.
    /// Do not dispose - this is shared across tests.
    /// </summary>
    public static RSA Rsa2048 => s_rsa2048.Value;

    /// <summary>
    /// Alternative RSA 2048-bit key for tests that need two different keys.
    /// Do not dispose - this is shared across tests.
    /// </summary>
    public static RSA Rsa2048Alt => s_rsa2048Alt.Value;

    /// <summary>
    /// Shared RSA 4096-bit key for tests requiring larger keys.
    /// Do not dispose - this is shared across tests.
    /// </summary>
    public static RSA Rsa4096 => s_rsa4096.Value;

    /// <summary>
    /// Shared ECDSA P-256 key for tests using elliptic curve cryptography.
    /// Do not dispose - this is shared across tests.
    /// </summary>
    public static ECDsa EcdsaP256 => s_ecdsaP256.Value;

    /// <summary>
    /// Shared ECDSA P-384 key for tests using elliptic curve cryptography.
    /// Do not dispose - this is shared across tests.
    /// </summary>
    public static ECDsa EcdsaP384 => s_ecdsaP384.Value;

    /// <summary>
    /// Gets an RsaSecurityKey from the shared RSA 2048 key.
    /// </summary>
    /// <param name="keyId">Optional key ID. Defaults to "test-rsa-key".</param>
    public static RsaSecurityKey GetRsaSecurityKey(string keyId = "test-rsa-key")
    {
        return new RsaSecurityKey(Rsa2048) { KeyId = keyId };
    }

    /// <summary>
    /// Gets an alternative RsaSecurityKey (different key material).
    /// Useful for tests that need to verify signature failures with wrong keys.
    /// </summary>
    /// <param name="keyId">Optional key ID. Defaults to "test-rsa-key-alt".</param>
    public static RsaSecurityKey GetRsaSecurityKeyAlt(string keyId = "test-rsa-key-alt")
    {
        return new RsaSecurityKey(Rsa2048Alt) { KeyId = keyId };
    }

    /// <summary>
    /// Gets SigningCredentials using the shared RSA key with RS256.
    /// </summary>
    /// <param name="keyId">Optional key ID. Defaults to "test-rsa-key".</param>
    public static SigningCredentials GetRsaSigningCredentials(string keyId = "test-rsa-key")
    {
        return new SigningCredentials(GetRsaSecurityKey(keyId), SecurityAlgorithms.RsaSha256);
    }

    /// <summary>
    /// Gets SigningCredentials using the alternative RSA key with RS256.
    /// </summary>
    /// <param name="keyId">Optional key ID. Defaults to "test-rsa-key-alt".</param>
    public static SigningCredentials GetRsaSigningCredentialsAlt(string keyId = "test-rsa-key-alt")
    {
        return new SigningCredentials(GetRsaSecurityKeyAlt(keyId), SecurityAlgorithms.RsaSha256);
    }

    /// <summary>
    /// Gets an ECDsaSecurityKey from the shared P-256 key.
    /// </summary>
    /// <param name="keyId">Optional key ID. Defaults to "test-ecdsa-key".</param>
    public static ECDsaSecurityKey GetEcdsaSecurityKey(string keyId = "test-ecdsa-key")
    {
        return new ECDsaSecurityKey(EcdsaP256) { KeyId = keyId };
    }

    /// <summary>
    /// Gets SigningCredentials using the shared ECDSA P-256 key with ES256.
    /// </summary>
    /// <param name="keyId">Optional key ID. Defaults to "test-ecdsa-key".</param>
    public static SigningCredentials GetEcdsaSigningCredentials(string keyId = "test-ecdsa-key")
    {
        return new SigningCredentials(GetEcdsaSecurityKey(keyId), SecurityAlgorithms.EcdsaSha256);
    }

    /// <summary>
    /// Creates a JsonWebKey from the shared RSA key for use in JWKS responses.
    /// </summary>
    /// <param name="keyId">Optional key ID. Defaults to "test-rsa-key".</param>
    public static JsonWebKey GetRsaJsonWebKey(string keyId = "test-rsa-key")
    {
        var securityKey = GetRsaSecurityKey(keyId);
        return JsonWebKeyConverter.ConvertFromRSASecurityKey(securityKey);
    }

    /// <summary>
    /// Creates a DPoP proof JWT header with the embedded JWK.
    /// This is a common pattern in DPoP tests.
    /// </summary>
    /// <param name="signingCredentials">Signing credentials to use.</param>
    /// <param name="jwk">The JWK to embed in the header.</param>
    public static JwtHeader CreateDPoPHeader(SigningCredentials signingCredentials, JsonWebKey jwk)
    {
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        header["jwk"] = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                kty = jwk.Kty,
                n = jwk.N,
                e = jwk.E,
                alg = jwk.Alg ?? "RS256"
            }));
        return header;
    }

    /// <summary>
    /// Creates a complete DPoP proof helper with pre-configured key, credentials, and JWK.
    /// </summary>
    /// <param name="keyId">Optional key ID for the key pair.</param>
    public static DPoPTestBundle CreateDPoPBundle(string keyId = "test-dpop-key")
    {
        var securityKey = GetRsaSecurityKey(keyId);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(securityKey);
        return new DPoPTestBundle(securityKey, signingCredentials, jwk);
    }

    /// <summary>
    /// Creates a DPoP test bundle using the alternative key (for wrong-key tests).
    /// </summary>
    public static DPoPTestBundle CreateDPoPBundleAlt(string keyId = "test-dpop-key-alt")
    {
        var securityKey = GetRsaSecurityKeyAlt(keyId);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(securityKey);
        return new DPoPTestBundle(securityKey, signingCredentials, jwk);
    }

    /// <summary>
    /// Gets the RSA parameters from the shared key for building JWK JSON manually.
    /// </summary>
    public static RSAParameters GetRsaParameters(bool includePrivate = true)
    {
        return Rsa2048.ExportParameters(includePrivate);
    }

    /// <summary>
    /// Gets the RSA parameters from the alternative key.
    /// </summary>
    public static RSAParameters GetRsaParametersAlt(bool includePrivate = true)
    {
        return Rsa2048Alt.ExportParameters(includePrivate);
    }

    /// <summary>
    /// Creates a JWK JSON string from the shared RSA key (includes private key components).
    /// Useful for tests that need to sign JWTs with the full key.
    /// </summary>
    /// <param name="keyId">Optional key ID. Defaults to "test-rsa-key".</param>
    public static string GetRsaJwkJson(string keyId = "test-rsa-key")
    {
        var p = Rsa2048.ExportParameters(true);
        var n = Base64UrlEncoder.Encode(p.Modulus);
        var e = Base64UrlEncoder.Encode(p.Exponent);
        var d = Base64UrlEncoder.Encode(p.D);
        var pVal = Base64UrlEncoder.Encode(p.P);
        var q = Base64UrlEncoder.Encode(p.Q);
        var dp = Base64UrlEncoder.Encode(p.DP);
        var dq = Base64UrlEncoder.Encode(p.DQ);
        var qi = Base64UrlEncoder.Encode(p.InverseQ);
        return $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"kid\":\"{keyId}\",\"n\":\"{n}\",\"e\":\"{e}\",\"d\":\"{d}\",\"p\":\"{pVal}\",\"q\":\"{q}\",\"dp\":\"{dp}\",\"dq\":\"{dq}\",\"qi\":\"{qi}\"}}";
    }

    /// <summary>
    /// Creates a public-only JWK JSON string from the shared RSA key.
    /// Useful for client JWKS storage in tests.
    /// </summary>
    /// <param name="keyId">Optional key ID. Defaults to "test-rsa-key".</param>
    public static string GetRsaPublicJwkJson(string keyId = "test-rsa-key")
    {
        var p = Rsa2048.ExportParameters(false);
        var n = Base64UrlEncoder.Encode(p.Modulus);
        var e = Base64UrlEncoder.Encode(p.Exponent);
        return $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"kid\":\"{keyId}\",\"n\":\"{n}\",\"e\":\"{e}\"}}";
    }

    /// <summary>
    /// Creates a JWK JSON string from the alternative RSA key (includes private key components).
    /// </summary>
    /// <param name="keyId">Optional key ID. Defaults to "test-rsa-key-alt".</param>
    public static string GetRsaJwkJsonAlt(string keyId = "test-rsa-key-alt")
    {
        var p = Rsa2048Alt.ExportParameters(true);
        var n = Base64UrlEncoder.Encode(p.Modulus);
        var e = Base64UrlEncoder.Encode(p.Exponent);
        var d = Base64UrlEncoder.Encode(p.D);
        var pVal = Base64UrlEncoder.Encode(p.P);
        var q = Base64UrlEncoder.Encode(p.Q);
        var dp = Base64UrlEncoder.Encode(p.DP);
        var dq = Base64UrlEncoder.Encode(p.DQ);
        var qi = Base64UrlEncoder.Encode(p.InverseQ);
        return $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"kid\":\"{keyId}\",\"n\":\"{n}\",\"e\":\"{e}\",\"d\":\"{d}\",\"p\":\"{pVal}\",\"q\":\"{q}\",\"dp\":\"{dp}\",\"dq\":\"{dq}\",\"qi\":\"{qi}\"}}";
    }

    /// <summary>
    /// Creates a client assertion JWT and matching public JWK JSON.
    /// Common pattern for client_assertion tests.
    /// </summary>
    public static (string assertion, string publicJwkJson) CreateClientAssertion(string clientId, string tokenEndpoint, string keyId = "test-client-key")
    {
        var jwkJson = GetRsaJwkJson(keyId);
        var creds = new SigningCredentials(new JsonWebKey(jwkJson), SecurityAlgorithms.RsaSha256);
        var now = DateTime.UtcNow;
        var jti = Guid.NewGuid().ToString("N");
        var token = new JwtSecurityToken(
            issuer: clientId,
            audience: tokenEndpoint,
            claims: new[] { new System.Security.Claims.Claim("sub", clientId), new System.Security.Claims.Claim("jti", jti) },
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(5),
            signingCredentials: creds
        );
        var handler = new JwtSecurityTokenHandler();
        var assertion = handler.WriteToken(token);
        var publicJwkJson = GetRsaPublicJwkJson(keyId);
        return (assertion, publicJwkJson);
    }

    /// <summary>
    /// Creates a signed request object JWT and matching JWK JSON.
    /// Common pattern for JAR (JWT-Secured Authorization Request) tests.
    /// </summary>
    public static (string jwt, string kid, string jwkJson) CreateSignedRequestObject(
        string clientId,
        string audience,
        string keyId = "test-jar-key",
        string? redirectUri = "https://cb",
        string responseType = "code")
    {
        var jwkJson = GetRsaJwkJson(keyId);
        var creds = new SigningCredentials(new JsonWebKey(jwkJson), SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: clientId,
            audience: audience,
            claims: new[]
            {
                new System.Security.Claims.Claim("client_id", clientId),
                new System.Security.Claims.Claim("response_type", responseType),
                new System.Security.Claims.Claim("redirect_uri", redirectUri ?? "")
            },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds
        );
        var handler = new JwtSecurityTokenHandler();
        return (handler.WriteToken(token), keyId, jwkJson);
    }
}

/// <summary>
/// A bundle containing all the components needed for DPoP testing.
/// </summary>
public sealed record DPoPTestBundle(
    RsaSecurityKey SecurityKey,
    SigningCredentials SigningCredentials,
    JsonWebKey Jwk);
