using System.Security.Cryptography.X509Certificates;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Resolves mTLS certificate thumbprints for token binding.
/// </summary>
public interface IMtlsThumbprintResolver
{
    /// <summary>
    /// Resolves the SHA-256 thumbprint (x5t#S256) from the provided certificate.
    /// </summary>
    string? ResolveThumbprint(X509Certificate2? certificate);
}
