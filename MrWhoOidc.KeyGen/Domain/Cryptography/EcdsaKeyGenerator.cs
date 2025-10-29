using System.Security.Cryptography;

namespace MrWhoOidc.KeyGen.Domain.Cryptography;

/// <summary>
/// Generates ECDSA key pairs for OIDC client signing.
/// </summary>
public static class EcdsaKeyGenerator
{
    /// <summary>
    /// Generates an ECDSA key pair with the specified curve.
    /// </summary>
    /// <param name="curve">Curve name (P-256, P-384, or P-521).</param>
    /// <returns>ECDSA key pair.</returns>
    /// <exception cref="ArgumentException">Thrown when curve is invalid.</exception>
    public static ECDsa Generate(string curve)
    {
        var ecCurve = curve switch
        {
            "P-256" => ECCurve.NamedCurves.nistP256,
            "P-384" => ECCurve.NamedCurves.nistP384,
            "P-521" => ECCurve.NamedCurves.nistP521,
            _ => throw new ArgumentException(
                "Curve must be P-256, P-384, or P-521.",
                nameof(curve))
        };

        var ecdsa = ECDsa.Create(ecCurve);
        return ecdsa;
    }
}
