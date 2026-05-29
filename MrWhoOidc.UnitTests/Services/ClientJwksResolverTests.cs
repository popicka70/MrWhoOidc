using System;
using System.Linq;
using System.Security.Cryptography;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Services;

[TestClass]
public sealed class ClientJwksResolverTests
{
    private static readonly IClientJwksProvider Provider = new ClientJwksResolver();

    private static string PublicJwkJson(string kid, string alg = "RS256", string use = "sig")
    {
        using var rsa = RSA.Create(2048);
        return RsaJwk.FromRSA(rsa, kid, alg: alg, use: use).ToJson(includePrivate: false);
    }

    [TestMethod]
    public void ParseSecurityKeys_StandardJwks_ReturnsAllKeys()
    {
        var json = $"{{\"keys\":[{PublicJwkJson("a")},{PublicJwkJson("b")}]}}";

        var keys = Provider.ParseSecurityKeys(json);

        Assert.HasCount(2, keys);
    }

    [TestMethod]
    public void ParseSecurityKeys_RawJsonArray_TreatedAsJwks()
    {
        // A bare JSON array of JWK objects (no enclosing { "keys": ... }) should
        // be treated as a key set rather than rejected.
        var json = $"[{PublicJwkJson("a")},{PublicJwkJson("b")}]";

        var keys = Provider.ParseSecurityKeys(json);

        Assert.HasCount(2, keys);
    }

    [TestMethod]
    public void ParseSecurityKeys_SingleJwkObject_ReturnsOneKey()
    {
        var json = PublicJwkJson("solo");

        var keys = Provider.ParseSecurityKeys(json);

        Assert.HasCount(1, keys);
    }

    [TestMethod]
    public void ParseSecurityKeys_RawArrayIgnoresNonObjectElements()
    {
        // Non-object array entries must be skipped without throwing.
        var json = $"[{PublicJwkJson("a")},\"not-a-key\",123]";

        var keys = Provider.ParseSecurityKeys(json);

        Assert.HasCount(1, keys);
    }

    [TestMethod]
    public void ParseSecurityKeys_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.IsEmpty(Provider.ParseSecurityKeys(null));
        Assert.IsEmpty(Provider.ParseSecurityKeys("   "));
    }

    [TestMethod]
    public void ParseSecurityKeys_MalformedJson_ReturnsEmpty()
    {
        var keys = Provider.ParseSecurityKeys("{ not valid json");

        Assert.IsEmpty(keys);
    }
}
