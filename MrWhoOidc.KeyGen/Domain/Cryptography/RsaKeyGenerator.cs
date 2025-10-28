using System.Security.Cryptography;

namespace MrWhoOidc.KeyGen.Domain.Cryptography;

/// <summary>
/// Generates RSA key pairs for OIDC client signing.
/// </summary>
public static class RsaKeyGenerator
{
    /// <summary>
    /// Generates an RSA key pair with the specified key size.
    /// </summary>
    /// <param name="keySize">Key size in bits (2048, 3072, or 4096).</param>
    /// <returns>RSA key pair.</returns>
    /// <exception cref="ArgumentException">Thrown when key size is invalid.</exception>
    public static RSA Generate(int keySize)
    {
        if (keySize != 2048 && keySize != 3072 && keySize != 4096)
        {
            throw new ArgumentException(
                "Key size must be 2048, 3072, or 4096 bits.",
                nameof(keySize));
        }

        var rsa = RSA.Create(keySize);
        return rsa;
    }
}
