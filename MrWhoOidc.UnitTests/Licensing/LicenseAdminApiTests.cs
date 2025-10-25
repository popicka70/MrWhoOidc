using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;
using MrWhoOidc.WebAuth.Admin.Dto;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.UnitTests.Licensing;

[TestClass]
[DoNotParallelize]
public sealed class LicenseAdminApiTests
{
    private static readonly Guid DefaultTenantId = new("00000000-0000-0000-0000-000000000001");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [TestInitialize]
    public void SetUp()
    {
        TestAuthHandler.CurrentUserId = Guid.Empty;
        TestPlatformAdminHandler.IsPlatformAdmin = false;
    }

    [TestCleanup]
    public void TearDown()
    {
        TestAuthHandler.CurrentUserId = Guid.Empty;
        TestPlatformAdminHandler.IsPlatformAdmin = false;
    }

    [TestMethod]
    public async Task InstallLicense_ReturnsLicenseDto_WhenServiceSucceeds()
    {
        var userId = Guid.NewGuid();
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        var licenseInfo = new LicenseInfo(
            "enterprise",
            "Test Org",
            now.AddDays(-3),
            now.AddDays(30),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "feature-one" },
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["seats"] = 1000 },
            false,
            true);

        var serviceMock = new Mock<ILicenseService>(MockBehavior.Strict);
        serviceMock
            .Setup(s => s.GetCurrentLicenseAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LicenseInfo?)null);
        serviceMock
            .Setup(s => s.InstallLicenseAsync(
                "sample-license",
                It.Is<Guid?>(tenant => tenant.HasValue && tenant.Value == DefaultTenantId),
                It.Is<Guid?>(installer => installer.HasValue && installer.Value == userId),
                It.Is<string?>(notes => notes == "trimmed"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(LicenseValidationResult.Success(licenseInfo))
            .Verifiable();

        using var factory = CreateFactory(serviceMock, timeProvider);
        using var client = CreateClient(factory);

        TestAuthHandler.CurrentUserId = userId;

        var payload = new InstallLicenseRequest("sample-license", "  trimmed  ");
        var response = await client.PostAsJsonAsync("/admin/api/license", payload);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<LicenseInfoDto>(JsonOptions);
        Assert.IsNotNull(dto);
        Assert.AreEqual("enterprise", dto!.Tier);
        Assert.AreEqual("Test Org", dto.OrganizationName);
        Assert.AreEqual(now.AddDays(-3), dto.ValidFrom);
        Assert.AreEqual(now.AddDays(30), dto.ValidUntil);
        Assert.AreEqual(false, dto.IsExpired);
        Assert.AreEqual(true, dto.IsValid);
        Assert.AreEqual(30, dto.DaysUntilExpiry);
        CollectionAssert.AreEquivalent(new[] { "feature-one" }, dto.EnabledFeatures.ToArray());
        Assert.IsTrue(dto.Limits.TryGetValue("seats", out var limit) && limit == 1000);

        serviceMock.Verify();
    }

    [TestMethod]
    public async Task InstallLicense_ReturnsValidationError_WhenServiceFails()
    {
        var userId = Guid.NewGuid();
        var now = new DateTimeOffset(2025, 2, 2, 9, 30, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        var serviceMock = new Mock<ILicenseService>(MockBehavior.Strict);
        serviceMock
            .Setup(s => s.GetCurrentLicenseAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LicenseInfo?)null);
        serviceMock
            .Setup(s => s.InstallLicenseAsync(
                "bad-license",
                It.Is<Guid?>(tenant => tenant.HasValue && tenant.Value == DefaultTenantId),
                It.Is<Guid?>(installer => installer.HasValue && installer.Value == userId),
                It.Is<string?>(notes => notes == "note"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(LicenseValidationResult.Failure("invalid_signature", "Signature invalid"))
            .Verifiable();

        using var factory = CreateFactory(serviceMock, timeProvider);
        using var client = CreateClient(factory);

        TestAuthHandler.CurrentUserId = userId;

        var payload = new InstallLicenseRequest("bad-license", " note ");
        var response = await client.PostAsJsonAsync("/admin/api/license", payload);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<LicenseValidationErrorDto>(JsonOptions);
        Assert.IsNotNull(error);
        Assert.AreEqual("invalid_signature", error!.Error);
        Assert.AreEqual("Signature invalid", error.ErrorDescription);

        serviceMock.Verify();
    }

    [TestMethod]
    public async Task InstallLicense_AllowsTenantOverride_ForPlatformAdmins()
    {
        var userId = Guid.NewGuid();
        var requestedTenant = Guid.NewGuid();
        var now = new DateTimeOffset(2025, 3, 3, 15, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        var licenseInfo = new LicenseInfo(
            "professional",
            "Platform Org",
            now.AddDays(-1),
            now.AddDays(10),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "feature-x" },
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["users"] = 500 },
            false,
            true);

        var serviceMock = new Mock<ILicenseService>(MockBehavior.Strict);
        serviceMock
            .Setup(s => s.GetCurrentLicenseAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LicenseInfo?)null);
        serviceMock
            .Setup(s => s.InstallLicenseAsync(
                "platform-license",
                It.Is<Guid?>(tenant => tenant.HasValue && tenant.Value == requestedTenant),
                It.Is<Guid?>(installer => installer.HasValue && installer.Value == userId),
                It.Is<string?>(notes => notes == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(LicenseValidationResult.Success(licenseInfo))
            .Verifiable();

        using var factory = CreateFactory(serviceMock, timeProvider);
        using var client = CreateClient(factory);

        TestAuthHandler.CurrentUserId = userId;
        TestPlatformAdminHandler.IsPlatformAdmin = true;

        var payload = new InstallLicenseRequest("platform-license", null);
        var response = await client.PostAsJsonAsync($"/admin/api/license?tenantId={requestedTenant}", payload);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        serviceMock.Verify();
    }

    [TestMethod]
    public async Task ValidateLicense_ReturnsValidationResponse()
    {
        var now = new DateTimeOffset(2025, 4, 4, 8, 45, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        var licenseInfo = new LicenseInfo(
            "enterprise",
            "Validate Org",
            now.AddDays(-2),
            now.AddDays(20),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "feature-a", "feature-b" },
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["seats"] = 200 },
            false,
            true);

        var serviceMock = new Mock<ILicenseService>(MockBehavior.Strict);
        serviceMock
            .Setup(s => s.GetCurrentLicenseAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LicenseInfo?)null);
        serviceMock
            .Setup(s => s.ValidateLicenseKeyAsync("validate-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LicenseValidationResult.Success(licenseInfo))
            .Verifiable();

        using var factory = CreateFactory(serviceMock, timeProvider);
        using var client = CreateClient(factory);

        var payload = new ValidateLicenseRequest("validate-key");
        var response = await client.PostAsJsonAsync("/admin/api/license/validate", payload);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<LicenseValidationResponseDto>(JsonOptions);
        Assert.IsNotNull(dto);
        Assert.AreEqual(true, dto!.IsValid);
        Assert.IsNull(dto.ErrorCode);
        Assert.IsNull(dto.ErrorMessage);
        Assert.IsNotNull(dto.License);
        Assert.AreEqual("enterprise", dto.License!.Tier);
        Assert.AreEqual(20, dto.License.DaysUntilExpiry);

        serviceMock.Verify();
    }

    [TestMethod]
    public async Task GetLicenseHistory_ReturnsPagedEntries()
    {
        var now = new DateTimeOffset(2025, 5, 5, 18, 15, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var historyEntry = new LicenseHistoryEntry
        {
            Id = Guid.NewGuid(),
            LicenseId = Guid.NewGuid(),
            Action = "installed",
            OldTier = null,
            NewTier = "enterprise",
            Notes = "Initial install",
            Reason = "new",
            CreatedAt = now.AddMinutes(-30),
            CreatedBy = Guid.NewGuid(),
            UserAgent = "Mozilla/5.0",
            IpAddress = "127.0.0.1"
        };

        var paged = new PagedResult<LicenseHistoryEntry>(
            new[] { historyEntry },
            totalCount: 7,
            page: 2,
            pageSize: 5);

        var serviceMock = new Mock<ILicenseService>(MockBehavior.Strict);
        serviceMock
            .Setup(s => s.GetCurrentLicenseAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LicenseInfo?)null);
        serviceMock
            .Setup(s => s.GetLicenseHistoryAsync(
                It.Is<Guid?>(tenant => tenant.HasValue && tenant.Value == DefaultTenantId),
                2,
                5,
                "installed",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(paged)
            .Verifiable();

        using var factory = CreateFactory(serviceMock, timeProvider);
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/admin/api/license/history?page=2&pageSize=5&action=installed");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<LicenseHistoryResponseDto>(JsonOptions);
        Assert.IsNotNull(dto);
        Assert.AreEqual(7, dto!.TotalCount);
        Assert.AreEqual(2, dto.Page);
        Assert.AreEqual(5, dto.PageSize);
        Assert.AreEqual(2, dto.TotalPages);
        Assert.AreEqual(1, dto.Entries.Count);
        var entry = dto.Entries.Single();
        Assert.AreEqual(historyEntry.Id, entry.Id);
        Assert.AreEqual("installed", entry.Action);
        Assert.AreEqual("enterprise", entry.NewTier);
        Assert.AreEqual("Initial install", entry.Notes);
        Assert.AreEqual("Mozilla/5.0", entry.UserAgent);
        Assert.AreEqual("127.0.0.1", entry.IpAddress);

        serviceMock.Verify();
    }

    private static WebApplicationFactory<Program> CreateFactory(Mock<ILicenseService> serviceMock, TimeProvider timeProvider)
    {
        return TestWebAppFactory.CreateInMemory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                        options.DefaultChallengeScheme = TestAuthHandler.Scheme;
                        options.DefaultScheme = TestAuthHandler.Scheme;
                    }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });

                    services.RemoveAll<IAuthorizationHandler>();
                    services.AddSingleton<IAuthorizationHandler, AllowAllAdminHandler>();
                    services.AddSingleton<IAuthorizationHandler, AllowAllTenantAdminHandler>();
                    services.AddSingleton<IAuthorizationHandler, TestPlatformAdminHandler>();

                    services.AddSingleton<ILicenseService>(_ => serviceMock.Object);
                    services.AddSingleton(timeProvider);
                });
            });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthHandler.Scheme);
        return client;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long GetTimestamp() => _utcNow.UtcDateTime.Ticks;
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string Scheme = "Test";
        public static Guid CurrentUserId;

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = CurrentUserId == Guid.Empty ? Guid.NewGuid() : CurrentUserId;
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, "test-user")
            };
            var identity = new ClaimsIdentity(claims, Scheme);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class AllowAllAdminHandler : AuthorizationHandler<AdminRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class AllowAllTenantAdminHandler : AuthorizationHandler<TenantAdminRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantAdminRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class TestPlatformAdminHandler : AuthorizationHandler<PlatformAdminRequirement>
    {
        public static bool IsPlatformAdmin { get; set; }

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformAdminRequirement requirement)
        {
            if (IsPlatformAdmin)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
