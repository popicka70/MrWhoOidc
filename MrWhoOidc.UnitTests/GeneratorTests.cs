using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class GeneratorTests
{
    [TestMethod]
    public void ClientIdGenerator_Produces_UrlSafe_NoPadding()
    {
        var gen = new ClientIdGenerator();
        var id = gen.Generate(48);
        Assert.IsFalse(string.IsNullOrWhiteSpace(id));
        Assert.IsFalse(id.Contains('+') || id.Contains('/'));
        Assert.DoesNotContain('=', id);
        Assert.AreEqual(48, id.Length);
    }

    [TestMethod]
    public void ClientSecretGenerator_Produces_UrlSafe_NoPadding()
    {
        var gen = new ClientSecretGenerator();
        var secret = gen.Generate(32);
        Assert.IsFalse(string.IsNullOrWhiteSpace(secret));
        Assert.IsFalse(secret.Contains('+') || secret.Contains('/'));
        Assert.DoesNotContain('=', secret);
        Assert.IsGreaterThanOrEqualTo(32, secret.Length); // base64url expands
    }
}
