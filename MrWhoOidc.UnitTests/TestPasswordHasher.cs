using MrWhoOidc.Auth.Services;
using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.UnitTests;

public class TestPasswordHasher : IPasswordHasher
{
    public string Hash(string password)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(password);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    public bool Verify(string password, string hash)
    {
        return Hash(password) == hash;
    }
}
