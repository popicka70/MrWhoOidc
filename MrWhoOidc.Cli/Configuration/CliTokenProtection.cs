using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MrWhoOidc.Cli.Configuration;

/// <summary>
/// Encrypts token values at rest in <c>config.json</c>.
/// </summary>
internal interface ITokenProtector
{
    /// <summary>Prefix that marks a protected value in the config file, e.g. <c>dpapi:</c>.</summary>
    string Prefix { get; }

    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// Windows DPAPI (CurrentUser scope): only the same Windows user on the same machine can decrypt.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiTokenProtector : ITokenProtector
{
    // Application-specific entropy so other DPAPI consumers of the same user cannot trivially reuse blobs.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MrWhoOidc.Cli.Tokens.v1");

    public string Prefix => "dpapi:";

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// Chooses how token fields are stored. On Windows they are protected with DPAPI; on Linux/macOS
/// they stay plaintext inside the owner-only (0600) config file. OS keychain integration
/// (macOS Keychain, libsecret) is future work.
/// </summary>
internal static class CliTokenProtection
{
    // AsyncLocal so a test override never leaks into tests running in parallel.
    private static readonly AsyncLocal<ITokenProtector?> s_override = new();

    /// <summary>The protector for this platform, or null when values are stored as plaintext.</summary>
    internal static ITokenProtector? Current => s_override.Value ?? (OperatingSystem.IsWindows() ? new DpapiTokenProtector() : null);

    /// <summary>Test hook: replaces the platform protector until the returned scope is disposed.</summary>
    internal static IDisposable UseProtectorForTesting(ITokenProtector protector)
    {
        var previous = s_override.Value;
        s_override.Value = protector;
        return new RestoreScope(() => s_override.Value = previous);
    }

    internal static string Protect(string value)
    {
        var protector = Current;
        if (protector is null || value.Length == 0)
        {
            return value;
        }

        return protector.Prefix + Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>
    /// Returns the plaintext token. Legacy plaintext values pass through unchanged (they are re-written
    /// protected on the next save). A protected value that cannot be decrypted (other user/machine,
    /// corrupted, or no protector on this platform) yields null, i.e. the profile must log in again.
    /// </summary>
    internal static string? Unprotect(string value)
    {
        var protector = Current;
        var prefix = protector?.Prefix ?? "dpapi:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return value;
        }

        if (protector is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(value[prefix.Length..])));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>
/// JSON converter for token properties: protects on write, unprotects (or migrates plaintext) on read.
/// </summary>
internal sealed class ProtectedTokenJsonConverter : JsonConverter<string?>
{
    public override bool HandleNull => false;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return raw is null ? null : CliTokenProtection.Unprotect(raw);
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(CliTokenProtection.Protect(value));
    }
}
