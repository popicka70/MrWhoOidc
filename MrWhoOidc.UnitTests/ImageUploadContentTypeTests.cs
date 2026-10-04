using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class ImageUploadContentTypeTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10];
    private static readonly byte[] Gif = Encoding.ASCII.GetBytes("GIF89a....");
    private static readonly byte[] Webp = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");
    private static readonly byte[] Html = Encoding.UTF8.GetBytes("<html><script>alert(document.domain)</script></html>");
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

    [TestMethod]
    public void Detect_RecognizesOnlyWhitelistedRasterFormats()
    {
        Assert.AreEqual("image/png", ImageContentType.Detect(Png));
        Assert.AreEqual("image/jpeg", ImageContentType.Detect(Jpeg));
        Assert.AreEqual("image/gif", ImageContentType.Detect(Gif));
        Assert.AreEqual("image/webp", ImageContentType.Detect(Webp));
        Assert.IsNull(ImageContentType.Detect(Html));
        Assert.IsNull(ImageContentType.Detect(Svg));
        Assert.IsNull(ImageContentType.Detect(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVEfmt ")));
        Assert.IsNull(ImageContentType.Detect([]));
    }

    [TestMethod]
    public async Task TenantIconUpload_IgnoresClaimedContentType_AndRejectsNonImages()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenant = new Tenant { Slug = "icons", Name = "Icons", IssuerUri = "https://issuer/t/icons", Status = TenantStatus.Active };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var service = new TenantIconService(db, NullLogger<TenantIconService>.Instance);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.UploadIconAsync(tenant.Id, "x.png", "image/png", Html));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.UploadIconAsync(tenant.Id, "x.svg", "image/svg+xml", Svg));

        var iconId = await service.UploadIconAsync(tenant.Id, "x.png", "text/html", Png);
        var stored = await service.GetIconAsync(iconId);
        Assert.AreEqual("image/png", stored!.ContentType);
    }

    [TestMethod]
    public void ProviderLogoContentType_IsDerivedServerSide()
    {
        Assert.AreEqual("image/png", WebAuth.Infrastructure.EndpointMapping.EndpointMappingExtensions.ResolveProviderLogoContentType(Png, "text/html"));
        Assert.AreEqual("application/octet-stream", WebAuth.Infrastructure.EndpointMapping.EndpointMappingExtensions.ResolveProviderLogoContentType(Html, "text/html"));
        Assert.AreEqual("image/svg+xml", WebAuth.Infrastructure.EndpointMapping.EndpointMappingExtensions.ResolveProviderLogoContentType(Svg, "image/svg+xml"));
    }

    [TestMethod]
    public async Task ProviderLogoEndpoint_IsTenantScoped_AndSendsNosniff()
    {
        using var factory = (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory();
        var client = factory.CreateClient();

        Guid ownId, platformId, foreignId;
        using (var scope = factory.Services.CreateScope())
        {
            var tenantId = scope.ServiceProvider.GetRequiredService<ITenantAccessor>().CurrentTenant!.TenantId;
            using var db = new AuthDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AuthDbContext>>());
            var own = NewProvider(tenantId, "own-idp", Html, "text/html");
            var platform = NewProvider(null, "platform-idp", Png, "image/png");
            var foreign = NewProvider(Guid.NewGuid(), "foreign-idp", Png, "image/png");
            db.IdentityProviders.AddRange(own, platform, foreign);
            await db.SaveChangesAsync();
            (ownId, platformId, foreignId) = (own.Id, platform.Id, foreign.Id);
        }

        var foreignResp = await client.GetAsync($"/api/providers/{foreignId}/logo");
        Assert.AreEqual(HttpStatusCode.NotFound, foreignResp.StatusCode, "Another tenant's provider logo must not be served.");

        var platformResp = await client.GetAsync($"/api/providers/{platformId}/logo");
        Assert.AreEqual(HttpStatusCode.OK, platformResp.StatusCode);
        Assert.AreEqual("image/png", platformResp.Content.Headers.ContentType?.MediaType);

        var ownResp = await client.GetAsync($"/api/providers/{ownId}/logo");
        Assert.AreEqual(HttpStatusCode.OK, ownResp.StatusCode);
        Assert.AreEqual("application/octet-stream", ownResp.Content.Headers.ContentType?.MediaType, "Stored text/html must not be echoed back.");
        Assert.AreEqual("nosniff", ownResp.Headers.GetValues("X-Content-Type-Options").Single());
    }

    private static IdentityProvider NewProvider(Guid? tenantId, string name, byte[] logo, string contentType) => new()
    {
        TenantId = tenantId,
        Name = name + "-" + Guid.NewGuid().ToString("N")[..6],
        LogoStorageType = IdentityProviderLogoStorageType.Database,
        LogoData = logo,
        LogoContentType = contentType
    };
}
