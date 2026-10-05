using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// <c>Security:RejectPlaintextSecrets</c> (default <c>true</c>). Once the startup backfill
/// (<see cref="StoredSecretProtectionBackfill"/>) has protected every legacy row, a stored signing key or TOTP secret
/// without the <c>dp:v1:</c> prefix can only have been written around the application (e.g. directly into the
/// database), so reading it fails instead of trusting it. Set to <c>false</c> only to roll back.
/// </summary>
public sealed class SecretProtectionOptions
{
    public bool RejectPlaintextSecrets { get; set; } = true;
}

/// <summary>
/// Gate for <see cref="SecretProtectionOptions.RejectPlaintextSecrets"/>: plaintext is only rejected after this
/// process has run the backfill, so a host that skipped it (in-memory test hosts, failed migration) keeps reading
/// legacy rows instead of failing every sign-in.
/// </summary>
public sealed class PlaintextSecretPolicy(IOptions<SecretProtectionOptions>? options = null)
{
    private int _backfillCompleted;

    public bool RejectPlaintext
        => (options?.Value.RejectPlaintextSecrets ?? true) && Volatile.Read(ref _backfillCompleted) == 1;

    public void MarkBackfillCompleted() => Interlocked.Exchange(ref _backfillCompleted, 1);
}

/// <summary>A stored secret was plaintext after the backfill: treated as tampered, never used.</summary>
public sealed class PlaintextSecretRejectedException(string purpose)
    : InvalidOperationException($"Stored {purpose} is not protected (no dp:v1: prefix) and Security:RejectPlaintextSecrets is enabled; refusing to use it.");

public interface ISecretProtector
{
    string ProtectSigningKeyJwk(string plaintext);
    string UnprotectSigningKeyJwk(string storedValue);
    string ProtectTotpSecret(string plaintext);
    string? UnprotectTotpSecret(string? storedValue);
    /// <summary>Upstream IdP secrets inside <c>IdentityProvider.ConfigJson</c> (see <c>ProviderConfigSecrets</c>).</summary>
    string ProtectProviderSecret(string plaintext);
    string UnprotectProviderSecret(string storedValue);
    bool IsProtected(string? storedValue);
}

internal sealed class DataProtectionSecretProtector : ISecretProtector
{
    private const string Prefix = "dp:v1:";
    private readonly IDataProtector _signingKeyProtector;
    private readonly IDataProtector _totpProtector;
    private readonly IDataProtector _providerSecretProtector;
    private readonly PlaintextSecretPolicy? _policy;
    private readonly ILogger _logger;

    public DataProtectionSecretProtector(
        IDataProtectionProvider provider,
        PlaintextSecretPolicy? policy = null,
        ILogger<DataProtectionSecretProtector>? logger = null)
    {
        _signingKeyProtector = provider.CreateProtector("MrWhoOidc.Auth.SigningKeys.JwkJson.v1");
        _totpProtector = provider.CreateProtector("MrWhoOidc.Auth.TotpSecret.v1");
        _providerSecretProtector = provider.CreateProtector("MrWhoOidc.Auth.IdentityProviders.ConfigSecret.v1");
        _policy = policy;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public string ProtectSigningKeyJwk(string plaintext) => Protect(_signingKeyProtector, plaintext);

    public string UnprotectSigningKeyJwk(string storedValue)
    {
        var unprotected = Unprotect(_signingKeyProtector, storedValue, "signing key JWK");
        if (string.IsNullOrEmpty(unprotected))
        {
            throw new InvalidOperationException(
                "Failed to unprotect signing key JWK: stored value is missing, corrupt, or was tampered with.");
        }
        return unprotected;
    }

    public string ProtectTotpSecret(string plaintext) => Protect(_totpProtector, plaintext);

    // A rejected TOTP secret throws rather than reading as "missing": the MFA gates treat a missing secret as "no
    // MFA", so swallowing it would turn a tampered row into an MFA bypass.
    public string? UnprotectTotpSecret(string? storedValue) => Unprotect(_totpProtector, storedValue, "TOTP secret");

    public string ProtectProviderSecret(string plaintext) => Protect(_providerSecretProtector, plaintext);

    // Legacy plaintext provider secrets are still accepted here; the startup backfill protects them.
    public string UnprotectProviderSecret(string storedValue)
        => storedValue.StartsWith(Prefix, StringComparison.Ordinal)
            ? _providerSecretProtector.Unprotect(storedValue[Prefix.Length..])
            : storedValue;

    public bool IsProtected(string? storedValue) => storedValue?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    private static string Protect(IDataProtector protector, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext) || plaintext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plaintext;
        }

        return Prefix + protector.Protect(plaintext);
    }

    private string? Unprotect(IDataProtector protector, string? storedValue, string purpose)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return storedValue;
        }

        if (!storedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            if (_policy?.RejectPlaintext == true)
            {
                _logger.LogError("Refusing to use a plaintext {Purpose} found after the protection backfill (Security:RejectPlaintextSecrets)", purpose);
                throw new PlaintextSecretRejectedException(purpose);
            }

            return storedValue;
        }

        return protector.Unprotect(storedValue[Prefix.Length..]);
    }
}

public static class SecretProtectorExtensions
{
    /// <summary>
    /// Reads a stored upstream-IdP key (<c>IdentityProviderKey.Jwk</c>): protected values are unprotected, legacy
    /// plaintext and empty values are returned as stored, and a missing protector (tests) is a no-op.
    /// </summary>
    public static string UnprotectProviderKeyJwk(this ISecretProtector? protector, string storedValue)
        => protector is not null && protector.IsProtected(storedValue)
            ? protector.UnprotectSigningKeyJwk(storedValue)
            : storedValue;
}
