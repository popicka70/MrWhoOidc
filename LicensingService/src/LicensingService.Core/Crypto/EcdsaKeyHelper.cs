using System.Security.Cryptography;

namespace LicensingService.Core.Crypto;

/// <summary>
/// Generates ECDSA key pairs for license signing.
/// </summary>
public static class EcdsaKeyHelper
{
    /// <summary>
    /// Generates an ECDSA key pair with P-256 curve (ES256).
    /// </summary>
    /// <returns>ECDSA key pair.</returns>
    public static ECDsa GenerateP256Key()
    {
        return ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>
    /// Generates an ECDSA key pair with the specified curve.
    /// </summary>
    /// <param name="curve">Curve name (P-256, P-384, or P-521).</param>
    /// <returns>ECDSA key pair.</returns>
    /// <exception cref="ArgumentException">Thrown when curve is invalid.</exception>
    public static ECDsa GenerateKey(string curve)
    {
        var ecCurve = curve switch
        {
            "P-256" => ECCurve.NamedCurves.nistP256,
            "P-384" => ECCurve.NamedCurves.nistP384,
            "P-521" => ECCurve.NamedCurves.nistP521,
            _ => throw new ArgumentException("Curve must be P-256, P-384, or P-521.", nameof(curve))
        };

        return ECDsa.Create(ecCurve);
    }

    /// <summary>
    /// Loads an ECDSA private key from PEM format.
    /// </summary>
    /// <param name="pemKey">PEM-encoded private key.</param>
    /// <returns>ECDSA key pair.</returns>
    public static ECDsa LoadFromPem(string pemKey)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pemKey);
        return ecdsa;
    }

    /// <summary>
    /// Exports an ECDSA key to PEM format.
    /// </summary>
    /// <param name="ecdsa">ECDSA key pair.</param>
    /// <param name="includePrivate">Whether to include private key.</param>
    /// <returns>PEM-encoded key.</returns>
    public static string ExportToPem(ECDsa ecdsa, bool includePrivate = false)
    {
        if (includePrivate)
        {
            return ecdsa.ExportECPrivateKeyPem();
        }
        return ecdsa.ExportSubjectPublicKeyInfoPem();
    }
}
