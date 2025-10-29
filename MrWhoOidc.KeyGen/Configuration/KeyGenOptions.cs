namespace MrWhoOidc.KeyGen.Configuration;

/// <summary>
/// Configuration options for the Key and License Generation service.
/// </summary>
public class KeyGenOptions
{
    /// <summary>
    /// Gets the configuration section name for KeyGen options.
    /// </summary>
    public const string SectionName = "KeyGen";

    /// <summary>
    /// Path to the licensing private key PEM file used for signing license tokens.
    /// </summary>
    /// <remarks>
    /// This key should be an ECDSA P-256 private key in PEM format.
    /// Default paths:
    /// - Production: /secrets/licensing-key.pem
    /// - Development: secrets/licensing-key-dev.pem
    /// </remarks>
    public required string LicensingPrivateKeyPath { get; set; }
}
