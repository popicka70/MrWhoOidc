using Microsoft.AspNetCore.DataProtection;

namespace MrWhoOidc.Auth.Services;

public interface ISecretProtector
{
    string ProtectSigningKeyJwk(string plaintext);
    string UnprotectSigningKeyJwk(string storedValue);
    string ProtectTotpSecret(string plaintext);
    string? UnprotectTotpSecret(string? storedValue);
    bool IsProtected(string? storedValue);
}

internal sealed class DataProtectionSecretProtector : ISecretProtector
{
    private const string Prefix = "dp:v1:";
    private readonly IDataProtector _signingKeyProtector;
    private readonly IDataProtector _totpProtector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        _signingKeyProtector = provider.CreateProtector("MrWhoOidc.Auth.SigningKeys.JwkJson.v1");
        _totpProtector = provider.CreateProtector("MrWhoOidc.Auth.TotpSecret.v1");
    }

    public string ProtectSigningKeyJwk(string plaintext) => Protect(_signingKeyProtector, plaintext);

    public string UnprotectSigningKeyJwk(string storedValue) => Unprotect(_signingKeyProtector, storedValue) ?? string.Empty;

    public string ProtectTotpSecret(string plaintext) => Protect(_totpProtector, plaintext);

    public string? UnprotectTotpSecret(string? storedValue) => Unprotect(_totpProtector, storedValue);

    public bool IsProtected(string? storedValue) => storedValue?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    private static string Protect(IDataProtector protector, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext) || plaintext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plaintext;
        }

        return Prefix + protector.Protect(plaintext);
    }

    private static string? Unprotect(IDataProtector protector, string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return storedValue;
        }

        if (!storedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return storedValue;
        }

        return protector.Unprotect(storedValue[Prefix.Length..]);
    }
}
