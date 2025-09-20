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
}
