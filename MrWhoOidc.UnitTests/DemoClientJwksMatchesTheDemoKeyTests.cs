using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// The demo application signs its JAR request objects with the key pair from
/// <c>Examples/MrWhoOidc.RazorClient/keys</c>, and the seeder registers the matching public key for the
/// demo client. Both are committed to the repository on purpose (the demo demonstrates the mechanism, it
/// does not keep a secret), so they must not drift apart: a mismatch makes every request object of the demo
/// unverifiable and the JAR sign-in fails with "No JWK/JWKS configured for client".
/// </summary>
[TestClass]
public sealed class DemoClientJwksMatchesTheDemoKeyTests
{
    [TestMethod]
    public void TheSeededDemoPublicKeyIsThePublicPartOfTheDemoSigningKey()
    {
        var keyPath = Path.Combine(
            RepositoryRoot(),
            "Examples", "MrWhoOidc.RazorClient", "keys", "jar-signing-private.pem");

        Assert.IsTrue(File.Exists(keyPath), $"The demo's JAR signing key is missing: {keyPath}");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(keyPath));
        var parameters = rsa.ExportParameters(false);

        using var document = JsonDocument.Parse(Seeder.DemoClientPublicJwks);
        var registered = document.RootElement;

        Assert.AreEqual("RSA", registered.GetProperty("kty").GetString());
        Assert.AreEqual("RS256", registered.GetProperty("alg").GetString());
        Assert.AreEqual(
            Base64Url(parameters.Modulus!),
            registered.GetProperty("n").GetString(),
            "The seeded 'n' is not the modulus of the demo's signing key.");
        Assert.AreEqual(
            Base64Url(parameters.Exponent!),
            registered.GetProperty("e").GetString(),
            "The seeded 'e' is not the exponent of the demo's signing key.");
        Assert.AreEqual(
            KeyId(parameters.Modulus!),
            registered.GetProperty("kid").GetString(),
            "The seeded 'kid' is not the key id of the demo's signing key.");
    }

    /// <summary>The key id: the SHA-256 of the modulus, base64url encoded and shortened (the demo's rule).</summary>
    private static string KeyId(byte[] modulus)
        => Base64Url(SHA256.HashData(modulus))[..16];

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MrWhoOidc.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate the repository root (MrWhoOidc.slnx).");
        return directory!.FullName;
    }
}
