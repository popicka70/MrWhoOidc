using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AuthorizationCodeServiceTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [TestMethod]
    public async Task IssueAsync_PersistsCode_AndBuildsRedirect_WithCode()
    {
        using var db = CreateDb();
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var settingsService = new MockTenantSettingsService();
        var svc = new AuthorizationCodeService(db, meta, tenantAccessor, settingsService);
        var valid = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: "c1",
            RedirectUri: "https://app/cb",
            Scopes: new[] { "openid" },
            Nonce: "n",
            State: "s"
        );
        var (ok, err, redirect, code) = await svc.IssueAsync(valid, Guid.NewGuid());
        Assert.IsTrue(ok);
        Assert.IsNotNull(code);
        Assert.AreEqual(1, db.AuthorizationCodes.Count());
        StringAssert.Contains(redirect!, "code=");
        StringAssert.Contains(redirect!, "state=");
        // Metadata captured
        Assert.IsTrue(meta.TryGetAuthTime(code!, out _));
    }
}
