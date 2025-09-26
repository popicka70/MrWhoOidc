using System;
using System.Text;

namespace MrWhoOidc.WebAuth.Infrastructure;

/// <summary>
/// Centralized helpers for hashing identifiers into low-cardinality buckets suitable for metrics/logging.
/// Mirrors prior inline logic (SHA256 then first 8 bytes -> 16 hex chars) to avoid behavior changes.
/// </summary>
public static class Bucketization
{
    public static string Bucket(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    public static string BucketizeClientId(string clientId) => Bucket(clientId);

    public static string BucketizeAudience(string audience)
    {
        if (string.IsNullOrWhiteSpace(audience)) return "none";

        // Handle URNs first (e.g., urn:example:resource:sub:extra) before generic URI parsing, because
        // Uri.TryCreate treats URNs as absolute URIs with an empty Host which would otherwise cause us
        // to hash them instead of performing the deterministic truncation expected by tests.
        if (audience.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = audience.Split(':');
            if (parts.Length >= 3)
            {
                // Return first three segments (urn:example:resource)
                return string.Join(':', parts.AsSpan(0, 3).ToArray());
            }
            return audience;
        }

        if (Uri.TryCreate(audience, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrEmpty(uri.Host) ? Bucket(audience) : uri.Host.ToLowerInvariant();
        }

        return Bucket(audience);
    }
}
