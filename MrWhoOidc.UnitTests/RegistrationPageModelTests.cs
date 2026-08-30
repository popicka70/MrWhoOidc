using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Users;
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
        Assert.AreEqual("Platform Account Registration", fixture.Model.PageHeading);
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

    [TestMethod]
    public async Task OnPostCreateAsync_PlatformRegistrationPath_CreatesPlatformRegistrationUnderDefaultTenant()
    {
        var registrationService = new Mock<IRegistrationWorkflowService>();
        Guid? capturedTargetTenantId = null;
        bool? capturedIsPlatformRegistration = null;

        registrationService
            .Setup(service => service.CreateAndMaybeApproveRegistrationAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Guid?>(),
                It.IsAny<bool>(),
                It.IsAny<bool>()))
            .Callback<string, string?, string?, Guid?, string?, bool, bool, string?, string?, string?, CancellationToken, Guid?, bool, bool>(
                (_, _, _, _, _, _, _, _, _, _, _, targetTenantId, isPlatformRegistration, _) =>
                {
                    capturedTargetTenantId = targetTenantId;
                    capturedIsPlatformRegistration = isPlatformRegistration;
                })
            .ReturnsAsync(new RegistrationResult(Guid.NewGuid(), "pending", RegistrationOutcome.PendingCreated));

        var fixture = CreateModel(
            "/Registrations",
            queryString: null,
            TenantUserRegistrationMode.PlatformOnly,
            registrationService.Object);
        using var db = fixture.Db;
        var defaultTenantId = db.Tenants.Single().Id;

        fixture.Model.Input = new IndexModel.RegistrationInput
        {
            Email = "new.platform.user@example.com",
            FirstName = "Platform",
            LastName = "User"
        };

        var result = await fixture.Model.OnPostCreateAsync();

        var redirect = result as RedirectResult;
        Assert.IsNotNull(redirect);
        StringAssert.StartsWith(redirect!.Url, "/Registrations/Accepted?status=pending");
        Assert.AreEqual(defaultTenantId, capturedTargetTenantId);
        Assert.AreEqual(true, capturedIsPlatformRegistration);
    }

    private static (IndexModel Model, AuthDbContext Db) CreateModel(
        string path,
        string? queryString,
        TenantUserRegistrationMode registrationMode,
        IRegistrationWorkflowService? registrationService = null)
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
            registrationService ?? Mock.Of<IRegistrationWorkflowService>(),
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