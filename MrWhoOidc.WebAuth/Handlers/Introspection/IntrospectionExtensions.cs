using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Extension methods for introspection operations.
/// </summary>
internal static class IntrospectionExtensions
{
    /// <summary>
    /// Computes SHA256 hash of the token for storage/lookup.
    /// </summary>
    public static string ComputeTokenHash(this string token)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>
    /// Creates a privacy-preserving bucket identifier for a client ID.
    /// </summary>
    public static string BucketizeClientId(this string clientId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(clientId));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    /// <summary>
    /// Safely converts a string to long, returning null if invalid.
    /// </summary>
    public static long? ToLongOrNull(this string? value)
    {
        return long.TryParse(value, out var result) ? result : null;
    }

    /// <summary>
    /// Creates metric tags for a client.
    /// </summary>
    public static KeyValuePair<string, object?>[] CreateMetricTags(this string clientBucket)
    {
        return new[] { new KeyValuePair<string, object?>("client", clientBucket) };
    }
}
