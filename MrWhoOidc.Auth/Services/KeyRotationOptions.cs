using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Services;

public sealed class KeyRotationOptions
{
    public bool Enabled { get; set; } = true;

    // RSA key size used for generated signing and encryption keys.
    public int RsaKeySizeBits { get; set; } = 3072;

    // JWT signing algorithm used when generating new signing keys (initial generation and rotation).
    // Examples: RS256, RS384, RS512, PS256, PS384, PS512, ES256, ES384, ES512
    public string SigningAlgorithm { get; set; } = SecurityConstants.JwtAlgorithms.RS256;

    public TimeSpan RotationInterval { get; set; } = TimeSpan.FromDays(7);
    // Publish retired keys in JWKS for this overlap duration so existing tokens validate
    public TimeSpan Overlap { get; set; } = TimeSpan.FromDays(2);
    // How often to check rotation conditions
    public TimeSpan CheckPeriod { get; set; } = TimeSpan.FromHours(1);
}
