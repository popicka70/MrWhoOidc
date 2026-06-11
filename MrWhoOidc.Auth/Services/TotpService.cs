using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.Auth.Services;

public interface ITotpService
{
    string GenerateSecretBase32(int size = 20);
    string GetProvisioningUri(string secretBase32, string account, string issuer, int digits = 6, int period = 30, string algo = "SHA1");
    bool VerifyCode(string secretBase32, string code, int digits = 6, int period = 30, int window = 1, string algo = "SHA1");
}

internal sealed class TotpService : ITotpService
{
    public string GenerateSecretBase32(int size = 20)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        return Base32Encode(bytes);
    }

    public string GetProvisioningUri(string secretBase32, string account, string issuer, int digits = 6, int period = 30, string algo = "SHA1")
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        var issuerEsc = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={secretBase32}&issuer={issuerEsc}&algorithm={algo}&digits={digits}&period={period}";
    }

    public bool VerifyCode(string secretBase32, string code, int digits = 6, int period = 30, int window = 1, string algo = "SHA1")
    {
        if (string.IsNullOrWhiteSpace(secretBase32) || string.IsNullOrWhiteSpace(code)) return false;
        if (!int.TryParse(code, out var provided) || code.Length != digits) return false;

        var secret = Base32Decode(secretBase32);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var step = now / period;
        // Evaluate every window position and compare in constant time, without early-return,
        // so neither the value comparison nor the matching window index leaks via timing.
        var matched = false;
        var providedBytes = BitConverter.GetBytes(provided);
        for (long i = -window; i <= window; i++)
        {
            var ctr = step + i;
            var expected = ComputeHotp(secret, unchecked((ulong)ctr), digits, algo);
            if (CryptographicOperations.FixedTimeEquals(BitConverter.GetBytes(expected), providedBytes))
            {
                matched = true;
            }
        }
        return matched;
    }

    static int ComputeHotp(byte[] key, ulong counter, int digits, string algo)
    {
        using HMAC hmac = algo.ToUpperInvariant() switch
        {
            "SHA1" => new HMACSHA1(key),
            "SHA256" => new HMACSHA256(key),
            "SHA512" => new HMACSHA512(key),
            _ => new HMACSHA1(key)
        };
        var ctrBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(ctrBytes);
        var hash = hmac.ComputeHash(ctrBytes);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24)
                   | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8)
                   | (hash[offset + 3] & 0xFF);
        var otp = binary % (int)Math.Pow(10, digits);
        return otp;
    }

    static string Base32Encode(ReadOnlySpan<byte> bytes)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((bytes.Length + 7) * 8 / 5);
        int bits = 0;
        int value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(value >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }
        if (bits > 0)
        {
            output.Append(Alphabet[(value << (5 - bits)) & 0x1F]);
        }
        return output.ToString();
    }

    static byte[] Base32Decode(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        int bits = 0;
        int value = 0;
        var output = new List<byte>(input.Length * 5 / 8);
        foreach (var c in input.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(c) || c == '=') continue;
            int idx = Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException("Invalid base32");
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
