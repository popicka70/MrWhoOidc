using System.Security.Cryptography;
using System.Text;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>
/// Normalizes caller-supplied store keys. Keys are normally short, fully qualified strings
/// (for example <c>dpop:replay:{jkt}:{jti}</c>), but replay keys embed attacker-controlled
/// identifiers such as <c>jti</c> and client IDs. Oversized keys are replaced by a deterministic
/// SHA-256 digest so a long identifier cannot make every request fail against the store's key
/// bound (512 characters).
/// </summary>
internal static class DistributedStateKey
{
    private const string HashPrefix = "sha256:";

    public static string Normalize(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length <= DistributedStateEntry.MaxKeyLength)
        {
            return key;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return HashPrefix + Convert.ToHexString(digest).ToLowerInvariant();
    }
}
