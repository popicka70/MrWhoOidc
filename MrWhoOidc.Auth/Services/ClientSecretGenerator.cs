using System.Security.Cryptography;

namespace MrWhoOidc.Auth.Services;

public interface IClientSecretGenerator
{
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
