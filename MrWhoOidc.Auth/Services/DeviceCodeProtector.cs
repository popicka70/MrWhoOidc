using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// <c>Security:LookupHashKey</c>: the secret that keys the hash of the device authorization grant's user code.
/// The user code is short (8 characters by default), so an unkeyed hash could be enumerated offline from a
/// database dump. With this key the code is hashed with HMAC-SHA256; without it the code is protected reversibly
/// (DataProtection) and found by a bounded scan of the tenant's live device codes.
/// </summary>
public sealed class LookupHashOptions
{
    /// <summary>
    /// The key used to hash user codes. Leave it empty to fall back to reversible protection. A random string of
    /// at least 32 characters is recommended; it must be the same on every replica.
    /// </summary>
    public string LookupHashKey { get; set; } = string.Empty;
}

/// <summary>
/// M-Plain: the codes of the device authorization grant (RFC 8628) must not sit in the database in plaintext.
/// </summary>
public interface IDeviceCodeProtector
{
    /// <summary>The value stored for a device_code. It is also the lookup key.</summary>
    string ProtectDeviceCode(string deviceCode);

    /// <summary>The value stored for a user_code.</summary>
    string ProtectUserCode(string userCode);

    /// <summary>
    /// The lookup value of a user_code, or null when the user code cannot be looked up by its stored value
    /// (no keyed hash is configured, so the code is protected reversibly instead).
    /// </summary>
    string? UserCodeLookup(string userCode);

    /// <summary>
    /// Constant-time check of a user code against a stored value. The stored value is a keyed hash, a reversibly
    /// protected code, or (for rows written before the change) the code itself.
    /// </summary>
    bool MatchesUserCode(string? storedValue, string userCode);
}

public sealed class DeviceCodeProtector(
    IOptions<LookupHashOptions> options,
    ISecretProtector secretProtector,
    ILogger<DeviceCodeProtector> logger) : IDeviceCodeProtector
{
    /// <summary>The device_code has 256 bits of entropy, so an unkeyed hash is not enumerable offline.</summary>
    private const string DeviceCodePrefix = "sha256:v1:";

    /// <summary>The keyed hash of a user_code. The prefix tells it apart from a reversibly protected code.</summary>
    private const string UserCodeHashPrefix = "hmac:v1:";

    private readonly byte[]? _key = DeriveKey(options.Value.LookupHashKey, logger);

    public string ProtectDeviceCode(string deviceCode) =>
        DeviceCodePrefix + CryptoHelper.ComputeSha256Hex(deviceCode);

    public string ProtectUserCode(string userCode) =>
        _key is not null
            ? UserCodeHashPrefix + HmacHex(userCode)
            : secretProtector.ProtectDeviceUserCode(userCode);

    public string? UserCodeLookup(string userCode) =>
        _key is null ? null : UserCodeHashPrefix + HmacHex(userCode);

    public bool MatchesUserCode(string? storedValue, string userCode)
    {
        if (string.IsNullOrEmpty(storedValue))
        {
            return false;
        }

        if (storedValue.StartsWith(UserCodeHashPrefix, StringComparison.Ordinal))
        {
            // A row written before the key was configured cannot be matched this way.
            var expected = _key is null ? null : UserCodeHashPrefix + HmacHex(userCode);
            return expected is not null && FixedTimeEquals(expected, storedValue);
        }

        if (secretProtector.IsProtected(storedValue))
        {
            string unprotected;
            try
            {
                unprotected = secretProtector.UnprotectDeviceUserCode(storedValue);
            }
            catch (Exception ex)
            {
                // A value that cannot be unprotected is not a match; it must not fail the whole request.
                logger.LogWarning(ex, "A stored device user code could not be unprotected");
                return false;
            }

            return FixedTimeEquals(unprotected, userCode);
        }

        // Legacy row: the code itself was stored.
        return FixedTimeEquals(storedValue, userCode);
    }

    private string HmacHex(string userCode)
    {
        var key = _key!;
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(userCode));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[]? DeriveKey(string configured, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            logger.LogInformation(
                "Security:LookupHashKey is not configured: device user codes are protected reversibly instead of being hashed with a key.");
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(configured);
        if (bytes.Length < 32)
        {
            logger.LogWarning(
                "Security:LookupHashKey is shorter than 32 bytes: device user codes are hashed with it, but a random value of at least 32 bytes is recommended.");
        }

        return bytes;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
