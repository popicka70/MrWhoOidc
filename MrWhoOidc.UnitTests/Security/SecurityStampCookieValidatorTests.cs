using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Infrastructure.Security;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// H5 of the 2026-10-04 post-Phase-0 review. The validator keyed the account lookup on NameIdentifier, the
/// per-tenant User.Id, which only equals UserAccount.Id in the home tenant. Stamped cookies in any other
/// tenant were therefore signed out, which is why tenant switching and QR login issued stamp-less cookies.
/// Stamp-less cookies survive a password reset.
/// </summary>
[TestClass]
public sealed class SecurityStampCookieValidatorTests
{
    private static async Task<(AuthDbContext Db, UserAccount Account, User SecondaryUser)> SeedAsync()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var account = new UserAccount { Username = "alice", Email = "alice@example.com", NormalizedEmail = "alice@example.com", PasswordHash = "h", SecurityStamp = "stamp-1" };
        var secondary = new User { TenantId = Guid.NewGuid(), Username = "alice-b", Email = "alice@example.com" };
        db.UserAccounts.Add(account);
        db.Users.Add(secondary);
        await db.SaveChangesAsync();
        return (db, account, secondary);
    }

    private static CookieValidatePrincipalContext CreateContext(AuthDbContext db, params Claim[] claims)
    {
        var auth = new Mock<IAuthenticationService>();
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(auth.Object)
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return new CookieValidatePrincipalContext(
            http,
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(principal, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    [TestMethod]
    public async Task SecondTenantCookie_WithCurrentStamp_IsKept()
    {
        var (db, account, secondary) = await SeedAsync();
        using var _ = db;
        var ctx = CreateContext(db,
            new Claim(ClaimTypes.NameIdentifier, secondary.Id.ToString()),
            new Claim(UserClaimTypes.UserAccountId, account.Id.ToString()),
            new Claim(SecurityStampCookieValidator.SecurityStampClaimType, "stamp-1"));

        await SecurityStampCookieValidator.ValidateAsync(ctx);

        Assert.IsNotNull(ctx.Principal, "a valid session in the user's second tenant must not be signed out");
    }

    [TestMethod]
    public async Task SecondTenantCookie_AfterPasswordReset_IsRejected()
    {
        var (db, account, secondary) = await SeedAsync();
        using var _ = db;
        account.SecurityStamp = "stamp-2";
        await db.SaveChangesAsync();
        var ctx = CreateContext(db,
            new Claim(ClaimTypes.NameIdentifier, secondary.Id.ToString()),
            new Claim(UserClaimTypes.UserAccountId, account.Id.ToString()),
            new Claim(SecurityStampCookieValidator.SecurityStampClaimType, "stamp-1"));

        await SecurityStampCookieValidator.ValidateAsync(ctx);

        Assert.IsNull(ctx.Principal, "a stamp rotated by a password reset must end the session");
    }

    [TestMethod]
    public async Task HomeTenantCookie_WithoutAccountIdClaim_StillValidatesByNameIdentifier()
    {
        var (db, account, _) = await SeedAsync();
        using var __ = db;
        var ctx = CreateContext(db,
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new Claim(SecurityStampCookieValidator.SecurityStampClaimType, "stale"));

        await SecurityStampCookieValidator.ValidateAsync(ctx);

        Assert.IsNull(ctx.Principal);
    }

    // Third 2026-10-04 review: accounts created by the provisioner had no stamp, so their sessions carried no stamp
    // claim and the validator skipped them, even after a password reset had since set a stamp.

    [TestMethod]
    public async Task StamplessCookie_AfterTheAccountGotAStamp_IsRejected()
    {
        var (db, account, secondary) = await SeedAsync();
        using var _ = db;
        secondary.UserAccountId = account.Id;
        await db.SaveChangesAsync();
        var ctx = CreateContext(db, new Claim(ClaimTypes.NameIdentifier, secondary.Id.ToString()));

        await SecurityStampCookieValidator.ValidateAsync(ctx);

        Assert.IsNull(ctx.Principal, "a cookie that never carried the account's stamp predates it and must end");
    }

    [TestMethod]
    public async Task StamplessCookie_ForAnAccountWithoutStamp_IsKept()
    {
        var (db, account, secondary) = await SeedAsync();
        using var _ = db;
        account.SecurityStamp = null;
        secondary.UserAccountId = account.Id;
        await db.SaveChangesAsync();
        var ctx = CreateContext(db, new Claim(ClaimTypes.NameIdentifier, secondary.Id.ToString()));

        await SecurityStampCookieValidator.ValidateAsync(ctx);

        Assert.IsNotNull(ctx.Principal, "nothing has been rotated yet, so there is nothing to compare against");
    }
}
