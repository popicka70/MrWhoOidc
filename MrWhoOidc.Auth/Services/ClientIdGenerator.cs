using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for generating secure, URL-safe client IDs.
/// </summary>
public interface IClientIdGenerator
{
    /// <summary>
    /// Generates a new client ID.
    /// </summary>
    /// <param name="length">The desired length of the ID.</param>
    /// <returns>A secure random string.</returns>
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
