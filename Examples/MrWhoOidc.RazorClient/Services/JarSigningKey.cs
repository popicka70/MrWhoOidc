using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.RazorClient.Services;

/// <summary>
/// The key pair the demo uses to sign its JAR request objects.
/// <para>
/// The identity provider verifies a request object only against the key that is registered for the client
/// (it never sees the client secret in plain text, so it cannot verify an HMAC-signed request object). The
/// demo therefore signs with its own RSA key and publishes the public part as a JWK set; the identity
/// provider registers that public key for the demo client (see the seeder).
/// </para>
/// <para>
/// The key pair is committed to the repository on purpose: the demo exists to demonstrate the mechanism, not
/// to keep a secret. Keeping it stable also means the identity provider can cache the public key.
/// </para>
/// </summary>
public sealed class JarSigningKey : IDisposable
{
    public const string DefaultKeyFileName = "jar-signing-private.pem";

    private readonly RSA _rsa;

    public JarSigningKey(IConfiguration configuration, ILogger<JarSigningKey> logger)
    {
        var path = configuration["MrWhoOidc:JarSigningKey:Path"];
        if (string.IsNullOrWhiteSpace(path))
        {
            var directory = configuration["MrWhoOidc:JarSigningKey:Directory"];
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.Combine(AppContext.BaseDirectory, "keys");
            }

            path = Path.Combine(directory, DefaultKeyFileName);
        }

        if (!File.Exists(path))
        {
            // The key is part of the demo (and of the repository), so a missing file is a broken deployment
            // and must not be papered over with a freshly generated key: the identity provider would not
            // know the new public key and every request object would be rejected.
            throw new FileNotFoundException(
                $"The JAR signing key of the demo is missing: {path}. It is part of the repository "
                + "(Examples/MrWhoOidc.RazorClient/keys).", path);
        }

        _rsa = RSA.Create();
        _rsa.ImportFromPem(File.ReadAllText(path));
        logger.LogInformation("Loaded the JAR signing key from {Path}", path);

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
