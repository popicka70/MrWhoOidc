using System;
using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.WebAuth.Observability;

public interface ICorrelationIdGenerator
{
    string GenerateCorrelationId();
    string GenerateHandle();
}

public static class CorrelationFormatting
{
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string EncodeCorrelationId(ReadOnlySpan<byte> data)
    {
        Span<char> buffer = stackalloc char[CalculateEncodedLength(data.Length)];
        var len = EncodeBase32(data, buffer);
        return new string(buffer[..len]);
    }

    public static string EncodeHandle(ReadOnlySpan<byte> data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string ShortHash(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexString(hash[..6]);
    }

    private static int CalculateEncodedLength(int byteCount)
        => (int)Math.Ceiling(byteCount / 5d * 8d);

    private static int EncodeBase32(ReadOnlySpan<byte> data, Span<char> output)
    {
        int buffer = 0;
        int bitsLeft = 0;
        int index = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                var value = (buffer >> bitsLeft) & 0x1F;
                output[index++] = CrockfordAlphabet[value];
            }
        }
        if (bitsLeft > 0)
        {
            var value = (buffer << (5 - bitsLeft)) & 0x1F;
            output[index++] = CrockfordAlphabet[value];
        }
        return index;
    }
}

public sealed class CorrelationIdGenerator : ICorrelationIdGenerator
{
    public string GenerateCorrelationId()
    {
        Span<byte> bytes = stackalloc byte[16]; // 128 bits
        RandomNumberGenerator.Fill(bytes);
        return CorrelationFormatting.EncodeCorrelationId(bytes);
    }

    public string GenerateHandle()
    {
        Span<byte> bytes = stackalloc byte[12]; // 96 bits
        RandomNumberGenerator.Fill(bytes);
        return CorrelationFormatting.EncodeHandle(bytes);
    }
}
