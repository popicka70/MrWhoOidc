using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.RazorClient.Services;

/// <summary>
/// The key pair the demo uses to sign its JAR request objects.
/// <para>
/// The identity provider verifies a request object only against the key that is registered for the client
/// (it never sees the client secret in plain text, so it cannot verify an HMAC-signed request object).
/// The demo therefore keeps its own RSA key pair, publishes the public part as a JWK set and signs the
/// request objects with the private part (RS256).
/// </para>
/// </summary>
public sealed class JarSigningKey : IDisposable
{
    private const string KeyFileName = "jar-signing-key.pem";
    private readonly RSA _rsa;

    public JarSigningKey(IConfiguration configuration, ILogger<JarSigningKey> logger)
    {
        var directory = configuration["MrWhoOidc:JarSigningKey:Directory"];
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(AppContext.BaseDirectory, "keys");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, KeyFileName);

        if (File.Exists(path))
        {
            _rsa = RSA.Create();
            _rsa.ImportFromPem(File.ReadAllText(path));
            logger.LogInformation("Loaded the JAR signing key from {Path}", path);
        }
        else
        {
            _rsa = RSA.Create(2048);
            File.WriteAllText(path, _rsa.ExportRSAPrivateKeyPem());
            logger.LogInformation("Created a new JAR signing key at {Path}", path);
        }

        var parameters = _rsa.ExportParameters(false);
        var keyId = ComputeKeyId(parameters);

        PublicJwk = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = SecurityAlgorithms.RsaSha256,
            ["kid"] = keyId,
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent)
        };

        SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
    }

    /// <summary>The credentials used to sign the request objects.</summary>
    public SigningCredentials SigningCredentials { get; }

    /// <summary>The public key of the pair, as a JWK.</summary>
    public IReadOnlyDictionary<string, object> PublicJwk { get; }

    /// <summary>The public key of the pair, as a JWK set (the shape a <c>jwks_uri</c> serves).</summary>
    public object PublicJwkSet => new { keys = new[] { PublicJwk } };

    private static string ComputeKeyId(RSAParameters parameters)
    {
        // A stable identifier for the key: the SHA-256 of the modulus, base64url encoded and shortened.
        var hash = SHA256.HashData(parameters.Modulus ?? Array.Empty<byte>());
        return Base64UrlEncoder.Encode(hash)[..16];
    }

    public void Dispose() => _rsa.Dispose();
}
