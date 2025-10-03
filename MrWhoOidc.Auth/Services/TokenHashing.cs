using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Token hashing utilities.
/// </summary>
public static class TokenHashing
{
    /// <summary>
    /// Compute SHA-256 hash of token value and return as Base64 string.
    /// Used for storing token hashes in database.
    /// </summary>
    public static string Compute(string value)
    {
        return CryptoHelper.ComputeSha256Base64(value);
    }

    /// <summary>
    /// Compute left-most half of SHA-256 and return base64url string.
    /// Used for at_hash, c_hash, and s_hash in ID tokens per OIDC spec.
    /// </summary>
    public static string ComputeLeftHalfBase64Url(string value)
    {
        return CryptoHelper.ComputeLeftHalfSha256Base64Url(value);
    }
}
