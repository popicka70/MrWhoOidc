using MrWhoOidc.Auth.Services;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace MrWhoOidc.UnitTests;

public class TestPasswordHasher : IPasswordHasher
{
    private const int Iterations = 600000;
    private const int SaltSize = 128 / 8; // 16 bytes
    private const int HashSize = 256 / 8; // 32 bytes

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var subkey = KeyDerivation.Pbkdf2(
            password: password,
            salt: salt,
            prf: KeyDerivationPrf.HMACSHA256,
            iterationCount: Iterations,
            numBytesRequested: HashSize);

        return $"v1:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(subkey)}";
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;

        var parts = hash.Split(':');
        if (parts.Length != 4 || parts[0] != "v1") return false;
        if (!int.TryParse(parts[1], out var iterations)) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var storedSubkey = Convert.FromBase64String(parts[3]);

            var actualSubkey = KeyDerivation.Pbkdf2(
                password,
                salt,
                KeyDerivationPrf.HMACSHA256,
                iterations,
                HashSize);

            return CryptographicOperations.FixedTimeEquals(actualSubkey, storedSubkey);
        }
        catch
        {
            return false;
        }
    }
}
