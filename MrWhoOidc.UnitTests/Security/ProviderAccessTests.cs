using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Security;
using MrWhoOidc.WebAuth.Security.Admin;
using ProviderKeysPage = MrWhoOidc.WebAuth.Pages.Admin.ProviderKeys.IndexModel;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// V6 of the third 2026-10-04 review: platform-wide identity providers (TenantId == null) are visible in every tenant,
/// and the provider key, claim mapping and logo pages looked them up by id alone, so any tenant admin could replace
/// a platform provider's signing key or claim mappings.
/// </summary>
[TestClass]
public sealed class ProviderAccessTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    private static ITenantAccessor TenantAccessorFor(Guid tenantId)
    {
        var accessor = new TenantAccessor();
        accessor.SetTenant(new TenantContext { TenantId = tenantId, Slug = "a", Name = "A", IssuerUri = "https://idp/t/a", IsMultiTenantMode = true });
        return accessor;
    }

    private static IAuthorizationService Authorization(bool platformAdmin)
    {
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), "platform-admin"))
            .ReturnsAsync(platformAdmin ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return auth.Object;
    }

    private static (AuthDbContext Db, Guid PlatformProvider, Guid OwnProvider) Seed()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var platform = new IdentityProvider { Name = "platform-entra", TenantId = null };
        var own = new IdentityProvider { Name = "tenant-a-google", TenantId = TenantA };
        db.IdentityProviders.AddRange(platform, own);
        db.SaveChanges();
        return (db, platform.Id, own.Id);
    }

    [TestMethod]
    public async Task TenantAdmin_CannotManagePlatformProvider_ButCanManageOwn()
    {
        var (db, platformProvider, ownProvider) = Seed();
        using var _ = db;
        var tenantAdmin = Authorization(platformAdmin: false);

        Assert.IsFalse(await ProviderAccess.CanManageAsync(platformProvider, db, TenantAccessorFor(TenantA), tenantAdmin, new ClaimsPrincipal(), default));
        Assert.IsTrue(await ProviderAccess.CanManageAsync(ownProvider, db, TenantAccessorFor(TenantA), tenantAdmin, new ClaimsPrincipal(), default));
    }

    [TestMethod]
    public async Task PlatformAdmin_CanManagePlatformProvider()
    {
        var (db, platformProvider, _) = Seed();
        using var __ = db;

        Assert.IsTrue(await ProviderAccess.CanManageAsync(platformProvider, db, TenantAccessorFor(TenantA), Authorization(platformAdmin: true), new ClaimsPrincipal(), default));
    }

    [TestMethod]
    public async Task ProviderKeysPage_TenantAdminOnPlatformProvider_IsNotFoundBeforeTheHandlerRuns()
    {
        var (db, platformProvider, _) = Seed();
        using var __ = db;
        var page = new ProviderKeysPage(db, Mock.Of<IPublicJwksCache>(), TenantAccessorFor(TenantA), Authorization(platformAdmin: false));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity("Cookies")) };
        var pageContext = new PageContext(new ActionContext(http, new RouteData(), new CompiledPageActionDescriptor()));
        page.PageContext = pageContext;
        var context = new PageHandlerExecutingContext(
            pageContext,
            [],
            new HandlerMethodDescriptor(),
            new Dictionary<string, object?> { ["providerId"] = platformProvider },
            page);
        var handlerRan = false;

        await page.OnPageHandlerExecutionAsync(context, () =>
        {
            handlerRan = true;
            return Task.FromResult<PageHandlerExecutedContext>(null!);
        });

        Assert.IsFalse(handlerRan, "the add/activate/delete/publish handler must not run");
        Assert.IsInstanceOfType<NotFoundResult>(context.Result);
    }
}
