using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.Auth.Services;

/// <summary>Authorization codes are stored as SHA-256 hashes so a database leak does not expose live codes.</summary>
public static class AuthorizationCodeHasher
{
    public static string Hash(string code) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
