using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.Auth.Services;

public interface IClientIdGenerator
{
    string Generate(int length = 24);
}

internal sealed class ClientIdGenerator : IClientIdGenerator
{
    // URL-safe Base64 without padding
    public string Generate(int length = 24)
    {
        if (length <= 0) length = 24;

        Span<byte> buffer = stackalloc byte[32];
        var sb = new StringBuilder(length);
        while (sb.Length < length)
        {
            RandomNumberGenerator.Fill(buffer);
            var chunk = Convert.ToBase64String(buffer).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var remaining = length - sb.Length;
            if (chunk.Length > remaining)
                sb.Append(chunk.AsSpan(0, remaining));
            else
                sb.Append(chunk);
        }
        return sb.ToString();
    }
}
