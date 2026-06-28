using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.TestDoubles;

/// <summary>
/// Stub implementation of ISecretProtector for unit tests.
/// Returns values unchanged (no actual protection).
/// </summary>
public sealed class StubSecretProtector : ISecretProtector
{
    public string ProtectSigningKeyJwk(string plaintext) => plaintext;
    public string UnprotectSigningKeyJwk(string storedValue) => storedValue;
    public string ProtectTotpSecret(string plaintext) => plaintext;
    public string? UnprotectTotpSecret(string? storedValue) => storedValue;
    public bool IsProtected(string? storedValue) => false;
}
