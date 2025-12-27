using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Implementation of IMtlsThumbprintResolver that computes SHA-256 thumbprints.
/// </summary>
public sealed class MtlsThumbprintResolver : IMtlsThumbprintResolver
{
    public string? ResolveThumbprint(X509Certificate2? certificate)
    {
        if (certificate == null) return null;

        // RFC 8705: x5t#S256 is the base64url-encoded SHA-256 hash of the DER encoding of the certificate.
        var rawData = certificate.RawData;
        var hash = SHA256.HashData(rawData);
        return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(hash);
    }
}
