using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.Auth.Services;

public static class TokenHashing
{
    public static string Compute(string value)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(bytes);
    }

    // Compute left-most half of SHA-256 and return base64url string, as required for at_hash/c_hash/s_hash
    public static string ComputeLeftHalfBase64Url(string value)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(value));
        // left-most half (16 bytes)
        var half = new byte[16];
        Array.Copy(bytes, half, 16);
        return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(half);
    }
}
