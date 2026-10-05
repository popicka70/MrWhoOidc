using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// mode=full exports upstream IdP client secrets and private signing keys. Any tenant admin could ask for it, and
/// so could a read-only support session, since the export is a GET.
/// </summary>
[TestClass]
public sealed class FullExportGateTests
{
    private static async Task<(IResult Result, Mock<IConfigurationExportService> Export)> ExportProviderAsync(string mode, bool platformAdmin, bool inSupportSession)
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var tenant = new Tenant { Slug = "acme", Name = "Acme", IssuerUri = "https://idp/t/acme" };
        var provider = new IdentityProvider { TenantId = tenant.Id, Name = "upstream", ConfigJson = """{"clientSecret":"s3cr3t"}""" };
        db.Tenants.Add(tenant);
        db.IdentityProviders.Add(provider);
        await db.SaveChangesAsync();

        var export = new Mock<IConfigurationExportService>();
        export.Setup(e => e.ExportIdentityProviderAsync(provider.Id, It.IsAny<ExportOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExportManifest());
        var support = new Mock<ITenantSupportAccessService>();
        support.Setup(s => s.IsSupportAccessActiveAsync(It.IsAny<HttpContext>())).ReturnsAsync(inSupportSession);
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), "platform-admin"))
            .ReturnsAsync(platformAdmin ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(support.Object).AddSingleton(authorization.Object).BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Cookies"))
        };
        http.Items["TenantId"] = tenant.Id;

        var result = await ExportImportHandler.ExportProvider(provider.Id, mode, db, export.Object, Mock.Of<ITenantAccessor>(), http, CancellationToken.None);
        return (result, export);
    }

    [TestMethod]
    [DataRow(false, false, DisplayName = "tenant admin")]
    [DataRow(true, true, DisplayName = "platform admin in a support session")]
    public async Task FullExport_IsForbidden(bool platformAdmin, bool inSupportSession)
    {
        var (result, export) = await ExportProviderAsync("full", platformAdmin, inSupportSession);

        Assert.AreEqual(StatusCodes.Status403Forbidden, (result as IStatusCodeHttpResult)?.StatusCode);
        export.Verify(e => e.ExportIdentityProviderAsync(It.IsAny<Guid>(), It.IsAny<ExportOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task FullExport_ByPlatformAdminOutsideSupport_IsAllowed()
    {
        var (_, export) = await ExportProviderAsync("full", platformAdmin: true, inSupportSession: false);

        export.Verify(e => e.ExportIdentityProviderAsync(It.IsAny<Guid>(), It.Is<ExportOptions>(o => o.Mode == ExportMode.Full), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ObfuscatedExport_ByTenantAdmin_IsAllowed()
    {
        var (_, export) = await ExportProviderAsync("obfuscated", platformAdmin: false, inSupportSession: false);

        export.Verify(e => e.ExportIdentityProviderAsync(It.IsAny<Guid>(), It.Is<ExportOptions>(o => o.Mode == ExportMode.Obfuscated), It.IsAny<CancellationToken>()), Times.Once);
    }
}
