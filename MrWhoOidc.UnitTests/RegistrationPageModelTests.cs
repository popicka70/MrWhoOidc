using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Pages.Registrations;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class RegistrationPageModelTests
{
    [TestMethod]
    public async Task OnGetAsync_TenantRegistrationPathWithPlatformOnlyMode_RedirectsToPlatformRegistration()
    {
        var fixture = CreateModel(
            "/t/default/Registrations",
            "?returnUrl=%2Faccount",
            TenantUserRegistrationMode.PlatformOnly);
        using var db = fixture.Db;

        var result = await fixture.Model.OnGetAsync();

        var redirect = result as RedirectResult;
        Assert.IsNotNull(redirect);
        Assert.AreEqual("/Registrations?returnUrl=%2Faccount", redirect!.Url);
    }

    [TestMethod]
    public async Task OnGetAsync_PlatformRegistrationPathWithPlatformOnlyMode_RendersPlatformRegistration()
    {
        var fixture = CreateModel(
            "/Registrations",
            queryString: null,
            TenantUserRegistrationMode.PlatformOnly);
        using var db = fixture.Db;

        var result = await fixture.Model.OnGetAsync();

        Assert.IsInstanceOfType(result, typeof(PageResult));
        Assert.IsFalse(fixture.Model.IsTenantRegistrationPath);
        Assert.IsTrue(fixture.Model.IsRegistrationAvailable);
        Assert.AreEqual("User Registration", fixture.Model.PageHeading);
    }

    [TestMethod]
    public async Task OnGetAsync_TenantRegistrationPathWithTenantOnlyMode_RendersTenantRegistration()
    {
        var fixture = CreateModel(
            "/t/default/Registrations",
            queryString: null,
            TenantUserRegistrationMode.TenantOnly);
        using var db = fixture.Db;

        var result = await fixture.Model.OnGetAsync();

        Assert.IsInstanceOfType(result, typeof(PageResult));
        Assert.IsTrue(fixture.Model.IsTenantRegistrationPath);
        Assert.IsTrue(fixture.Model.IsRegistrationAvailable);
    }

    private static (IndexModel Model, AuthDbContext Db) CreateModel(
        string path,
        string? queryString,
        TenantUserRegistrationMode registrationMode)
    {
        var tenantId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase($"registration-page-{Guid.NewGuid():N}")
            .Options;
        var db = new AuthDbContext(options);
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://localhost:5001/t/default",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();

        var tenantAccessor = new TenantAccessor();
        tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = tenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://localhost:5001/t/default",
            IsMultiTenantMode = true
        });

        var tenantSettings = new TenantSettings
        {
            Registration = new RegistrationTenantSettings
            {
                Mode = registrationMode
            }
        };
        var tenantSettingsService = new Mock<ITenantSettingsService>();
        tenantSettingsService
            .Setup(service => service.GetCurrentTenantSettingsAsync())
            .ReturnsAsync(tenantSettings);
        tenantSettingsService
            .Setup(service => service.GetTenantSettingsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(tenantSettings);

        var brandingService = new Mock<ITenantBrandingService>();
        brandingService
            .Setup(service => service.GetCurrentTenantBrandingAsync())
            .ReturnsAsync(new TenantBranding { TenantName = "Default Tenant" });

        var model = new IndexModel(
            Mock.Of<IPasswordHasher>(),
            Mock.Of<IRegistrationWorkflowService>(),
            Mock.Of<ITenantEnrollmentService>(),
            Mock.Of<ITenantDomainClaimService>(),
            Mock.Of<IReturnUrlClientContextResolver>(),
            db,
            tenantAccessor,
            tenantSettingsService.Object,
            brandingService.Object,
            new MultiTenancyOptions { Enabled = true, DefaultTenantSlug = "default" },
            NullLogger<IndexModel>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        if (!string.IsNullOrWhiteSpace(queryString))
        {
            httpContext.Request.QueryString = new QueryString(queryString);
        }

        model.PageContext = new PageContext
        {
            HttpContext = httpContext
        };

        return (model, db);
    }
}