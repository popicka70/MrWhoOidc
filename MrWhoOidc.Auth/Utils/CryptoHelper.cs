using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.Auth.Utils;

/// <summary>
/// Cryptographic utility methods for hashing and encoding.
/// Consolidates SHA-256 operations used throughout the codebase.
/// </summary>
public static class CryptoHelper
{
    /// <summary>
    /// Compute PKCE S256 code challenge from verifier (SHA-256 + Base64Url).
    /// Used for RFC 7636 PKCE validation.
    /// </summary>
    public static string ComputePkceS256(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(bytes);
    }

    /// <summary>
    /// Compute SHA-256 hash and return as Base64 string.
    /// Used for token hashing in database storage.
    /// </summary>
    public static string ComputeSha256Base64(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Compute SHA-256 hash and return as Base64Url string.
    /// Used for JWK thumbprints and other OIDC/OAuth2 URL-safe hashes.
    /// </summary>
    public static string ComputeSha256Base64Url(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(bytes);
    }

    /// <summary>
    /// Compute SHA-256 hash and return as lowercase hex string.
    /// Used for ETags, correlation IDs, and other hex-encoded hashes.
    /// </summary>
    public static string ComputeSha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Compute left-most half of SHA-256 and return base64url encoding.
    /// Used for at_hash, c_hash, and s_hash in ID tokens per OIDC spec.
    /// </summary>
    public static string ComputeLeftHalfSha256Base64Url(string value)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(value));
        var half = new byte[16];
        Array.Copy(bytes, half, 16);
        return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(half);
    }

    /// <summary>
    /// Compute left-most half of the hash associated with the given JWS alg and return base64url encoding.
    /// Used for at_hash, c_hash, and s_hash per OIDC/JARM specs.
    /// </summary>
    public static string ComputeLeftHalfHashBase64Url(string value, string jwsAlg)
    {
        if (string.IsNullOrWhiteSpace(jwsAlg))
        {
            // Backward-compatible default (RS256/ES256/etc).
            return ComputeLeftHalfSha256Base64Url(value);
        }

        var alg = jwsAlg.Trim().ToUpperInvariant();
        var input = Encoding.ASCII.GetBytes(value);

        if (alg.EndsWith("384", StringComparison.Ordinal))
        {
            var bytes = SHA384.HashData(input);
            var half = new byte[24];
            Array.Copy(bytes, half, half.Length);
            return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(half);
        }

        if (alg.EndsWith("512", StringComparison.Ordinal))
        {
            var bytes = SHA512.HashData(input);
            var half = new byte[32];
            Array.Copy(bytes, half, half.Length);
            return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(half);
        }

        // Default to SHA-256 family (RS256/PS256/ES256/HS256).
        return ComputeLeftHalfSha256Base64Url(value);
    }

    /// <summary>
    /// Compute SHA-256 and return first N bytes as hex string (for bucketing/short hashes).
    /// Used for client ID bucketing in metrics and logs.
    /// </summary>
    public static string ComputeSha256HexPrefix(string value, int byteCount = 6)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexString(hash[..byteCount]);
    }

    /// <summary>
    /// Compute SHA-256 hash and write to destination span.
    /// Used for in-place hashing without allocations.
    /// </summary>
    public static void ComputeSha256(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        SHA256.HashData(source, destination);
    }

    /// <summary>
    /// Generate a cryptographically secure random string of specified length.
    /// </summary>
    public static string GenerateSecureRandomString(int length, string choices = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%")
    {
        return RandomNumberGenerator.GetString(choices, length);
    }
}
