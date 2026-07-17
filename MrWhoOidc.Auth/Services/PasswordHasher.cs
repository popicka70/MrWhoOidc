using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Isopoh.Cryptography.Argon2;

namespace MrWhoOidc.Auth.Services;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

internal sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 128 / 8; // 16 bytes
    private const int HashSize = 256 / 8; // 32 bytes

    public string Hash(string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var config = new Argon2Config
            {
                Type = Argon2Type.HybridAddressing,
                Version = Argon2Version.Nineteen,
                TimeCost = 4,
                MemoryCost = 131072,
                Lanes = 4,
                Threads = 1,
                Password = passwordBytes,
                Salt = RandomNumberGenerator.GetBytes(SaltSize),
                HashLength = HashSize
            };

            using var argon2 = new Argon2(config);
            using var hash = argon2.Hash();
            return $"v2:{config.EncodeString(hash.Buffer)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;

        var idx = hash.IndexOf(':');
        if (idx < 0) return false;

        var version = hash[..idx];
        var rest = hash[(idx + 1)..];

        if (version == "v2")
        {
            return VerifyV2(password, rest);
        }

        if (version == "v1")
        {
            return VerifyV1(password, rest);
        }

        return false;
    }

    private static bool VerifyV2(string password, string encodedHash)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var config = new Argon2Config { Password = passwordBytes };
            if (!config.DecodeString(encodedHash, out var expectedHash) || expectedHash is null)
            {
                return false;
            }

            using (expectedHash)
            using (var argon2 = new Argon2(config))
            using (var actualHash = argon2.Hash())
            {
                return Argon2.FixedTimeEquals(actualHash, expectedHash);
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // Malformed/unparseable stored hash -> treat as non-matching.
            // Unexpected exceptions (e.g. OutOfMemoryException) are allowed to propagate.
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static bool VerifyV1(string password, string rest)
    {
        var parts = rest.Split(':');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], out var iterations)) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var storedSubkey = Convert.FromBase64String(parts[2]);

            var actualSubkey = KeyDerivation.Pbkdf2(
                password,
                salt,
                KeyDerivationPrf.HMACSHA256,
                iterations,
                HashSize);

            return CryptographicOperations.FixedTimeEquals(actualSubkey, storedSubkey);
        }
        catch (FormatException)
        {
            // Stored salt/subkey was not valid Base64 -> treat as non-matching.
            return false;
        }
    }
}
