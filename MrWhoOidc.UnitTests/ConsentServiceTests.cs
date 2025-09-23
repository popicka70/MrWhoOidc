using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class ConsentServiceTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [TestMethod]
    public async Task HasConsent_False_WhenNone()
    {
        using var db = CreateDb();
        var svc = new ConsentService(db);
        var ok = await svc.HasConsentAsync(Guid.NewGuid(), "c1", new[] { "openid", "email" });
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task GrantConsent_Then_HasConsent_True_ForRequestedScopes()
    {
        using var db = CreateDb();
        var u = Guid.NewGuid();
        var svc = new ConsentService(db);
        await svc.GrantConsentAsync(u, "c1", new[] { "openid", "email" });
        var ok = await svc.HasConsentAsync(u, "c1", new[] { "openid", "email" });
        Assert.IsTrue(ok);
        // Additional scope not granted should return false
        var ok2 = await svc.HasConsentAsync(u, "c1", new[] { "openid", "email", "profile" });
        Assert.IsFalse(ok2);
    }
}
