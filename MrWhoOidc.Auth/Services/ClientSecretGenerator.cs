using System.Security.Cryptography;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for generating secure client secrets.
/// </summary>
public interface IClientSecretGenerator
{
    /// <summary>
    /// Generates a new client secret.
    /// </summary>
    /// <param name="byteLength">The number of random bytes to generate.</param>
    /// <returns>A secure random string (base64url encoded).</returns>
    string Generate(int byteLength = 48); // 48 bytes -> 64-char base64url
}

internal sealed class ClientSecretGenerator : IClientSecretGenerator
{
    public string Generate(int byteLength = 48)
    {
        if (byteLength < 16) byteLength = 16; // minimum strength
        var bytes = new byte[byteLength];
        RandomNumberGenerator.Fill(bytes);
        var b64 = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return b64;
    }
}
