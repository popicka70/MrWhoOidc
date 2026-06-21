using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AuthorizationCodeMetadataStoreTests
{
    [TestMethod]
    public void Roundtrip_AuthTime_And_Resource()
    {
        var store = new InMemoryAuthorizationCodeMetadataStore();
        var code = "abc";
        var now = DateTimeOffset.UtcNow;
        store.SetAuthTime(code, now);
        store.SetResource(code, "https://api");

        Assert.IsTrue(store.TryGetAuthTime(code, out var at));
        Assert.AreEqual(now, at);
        Assert.IsTrue(store.TryGetResource(code, out var res));
        Assert.AreEqual("https://api", res);

        store.Remove(code);
        Assert.IsFalse(store.TryGetAuthTime(code, out _));
        Assert.IsFalse(store.TryGetResource(code, out _));
    }

    [TestMethod]
    public void Expired_Entries_Are_Not_Returned()
    {
        var now = DateTimeOffset.UtcNow;
        var current = now;
        var store = new InMemoryAuthorizationCodeMetadataStore(TimeSpan.FromSeconds(1), () => current);
        var code = "expiring";

        store.SetAuthTime(code, now);
        store.SetResource(code, "https://api");

        Assert.IsTrue(store.TryGetAuthTime(code, out _));
        Assert.IsTrue(store.TryGetResource(code, out _));

        current = now.AddSeconds(2);

        Assert.IsFalse(store.TryGetAuthTime(code, out _));
        Assert.IsFalse(store.TryGetResource(code, out _));
    }
}
