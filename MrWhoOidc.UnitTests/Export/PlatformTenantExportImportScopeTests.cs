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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.UnitTests.Export;

/// <summary>
/// H1: the platform export/import API is not under /platform-admin, so no system scope was opened for it and
/// the request resolved to the default tenant. A non-default tenant's export therefore omitted its realms,
/// clients and providers (the EF tenant filter hid them) and a cross-tenant import was rejected by the tenant
/// write guard ("Refusing to save ... for a different tenant").
/// <para>
/// These tests exercise the real request path (a second tenant exists, the request tenant is the default one)
/// and the real save path, so the cross-tenant scope and the write guard are both covered.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PlatformTenantExportImportScopeTests
{
    private const string SecondTenantSlug = "acme";

    /// <summary>A stable user id: the antiforgery token is bound to the authenticated identity.</summary>
    private const string TestUserId = "11111111-1111-1111-1111-111111111111";

    private const string TestUserName = "platform-scope-test-user";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [TestInitialize]
    public void SetUp() => ScopeTestAuthHandler.IsPlatformAdmin = true;

    [TestCleanup]
    public void TearDown() => ScopeTestAuthHandler.IsPlatformAdmin = false;

    private static WebApplicationFactory<Program> CreateFactory()
        => TestWebAppFactory.CreateInMemory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = ScopeTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = ScopeTestAuthHandler.SchemeName;
                    options.DefaultScheme = ScopeTestAuthHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, ScopeTestAuthHandler>(ScopeTestAuthHandler.SchemeName, _ => { });

                services.RemoveAll<IAuthorizationHandler>();
                services.AddSingleton<IAuthorizationHandler, ScopeAllowAllAdminHandler>();
                services.AddSingleton<IAuthorizationHandler, ScopeAllowAllTenantAdminHandler>();
                services.AddSingleton<IAuthorizationHandler, ScopePlatformAdminHandler>();
            });
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            // The antiforgery cookie is bound to HTTPS, like a browser.
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ScopeTestAuthHandler.SchemeName);
        return client;
    }

    /// <summary>
    /// Seeds a second (non-default) tenant with a realm, a client and a scope, plus a client in the default
    /// tenant that must never show up in the second tenant's export.
    /// </summary>
    private static async Task SeedSecondTenantAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        // The write guard pins writes to the request tenant; seeding another tenant is exactly the operation
        // that needs the explicit cross-tenant scope.
        using var crossTenant = TenantFilterScope.BeginCrossTenantScope();

        var existing = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Slug == SecondTenantSlug);
        if (existing is not null)
        {
            return;
        }

        var defaultTenant = await db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Slug == "default");
        var secondTenant = new Tenant
        {
            Slug = SecondTenantSlug,
            Name = "Acme Corp",
            IssuerUri = "https://localhost:5001/t/acme",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(secondTenant);

        var secondRealm = new Realm { TenantId = secondTenant.Id, Name = "acme", DisplayName = "Acme" };
        db.Realms.Add(secondRealm);

        var defaultRealm = await db.Realms.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.TenantId == defaultTenant.Id && r.Name == "default")
                           ?? new Realm { TenantId = defaultTenant.Id, Name = "default", DisplayName = "Default" };
        if (defaultRealm.Id == Guid.Empty || db.Entry(defaultRealm).State == EntityState.Detached)
        {
            db.Realms.Add(defaultRealm);
        }

        db.Clients.Add(new ClientEntity
        {
            TenantId = secondTenant.Id,
            ClientId = "acme-client",
            ClientName = "Acme Client",
            RealmId = secondRealm.Id,
            RequirePkce = true
        });

        db.Clients.Add(new ClientEntity
        {
            TenantId = defaultTenant.Id,
            ClientId = "default-only-client",
            ClientName = "Default Only",
            RealmId = defaultRealm.Id,
            RequirePkce = true
        });

        db.Scopes.Add(new Scope { Name = "acme.scope", TenantId = secondTenant.Id });

        await db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task Export_ForSecondTenant_ContainsThatTenantsConfiguration()
    {
        using var factory = CreateFactory();
        await SeedSecondTenantAsync(factory);
        using var client = CreateClient(factory);

        var response = await client.GetAsync($"/admin/api/platform/tenants/{SecondTenantSlug}/export");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            await response.Content.ReadAsStringAsync());

        var manifest = await ReadManifestAsync(response);
        var tenant = manifest.Data?.Tenants?.SingleOrDefault();
        Assert.IsNotNull(tenant, "the export must contain the requested tenant");
        Assert.AreEqual(SecondTenantSlug, tenant!.Slug);

        var realmNames = tenant.Realms?.Select(r => r.Name).ToArray() ?? [];
        CollectionAssert.Contains(realmNames, "acme",
            "the second tenant's realm must be present (it was hidden by the request tenant's filter)");

        var clientIds = tenant.Clients?.Select(c => c.ClientId).ToArray() ?? [];
        CollectionAssert.Contains(clientIds, "acme-client");
    }

    [TestMethod]
    public async Task Export_ForSecondTenant_DoesNotLeakTheRequestTenantsConfiguration()
    {
        using var factory = CreateFactory();
        await SeedSecondTenantAsync(factory);
        using var client = CreateClient(factory);

        var response = await client.GetAsync($"/admin/api/platform/tenants/{SecondTenantSlug}/export");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var manifest = await ReadManifestAsync(response);
        var tenant = manifest.Data?.Tenants?.SingleOrDefault();
        Assert.IsNotNull(tenant);

        var clientIds = tenant!.Clients?.Select(c => c.ClientId).ToArray() ?? [];
        CollectionAssert.DoesNotContain(clientIds, "default-only-client",
            "another tenant's client must never appear in the export");
    }

    [TestMethod]
    public async Task Export_ForUnknownTenant_ReturnsNotFound()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/admin/api/platform/tenants/does-not-exist/export");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Export_WithoutPlatformAdmin_IsForbidden()
    {
        using var factory = CreateFactory();
        await SeedSecondTenantAsync(factory);
        using var client = CreateClient(factory);

        ScopeTestAuthHandler.IsPlatformAdmin = false;
        var response = await client.GetAsync($"/admin/api/platform/tenants/{SecondTenantSlug}/export");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
            "a non platform-admin must not reach the cross-tenant export");
    }

    [TestMethod]
    public async Task Import_ForSecondTenant_WritesIntoThatTenant()
    {
        using var factory = CreateFactory();
        await SeedSecondTenantAsync(factory);
        using var client = CreateClient(factory);

        var manifest = new ExportManifest
        {
            Version = 1,
            ExportType = "tenant",
            ExportMode = "obfuscated",
            Data = new SeedManifest
            {
                Version = 1,
                Tenants =
                [
                    new TenantSeedDefinition
                    {
                        Slug = "imported-acme",
                        Name = "Imported Acme",
                        Realms =
                        [
                            new RealmSeedDefinition { Name = "imported-realm", DisplayName = "Imported Realm" }
                        ]
                    }
                ]
            }
        };

        var response = await PostImportAsync(client, factory, manifest);

        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        using var crossTenant = TenantFilterScope.BeginCrossTenantScope();

        var importedTenant = await db.Tenants.IgnoreQueryFilters().SingleOrDefaultAsync(t => t.Slug == "imported-acme");
        Assert.IsNotNull(importedTenant,
            "the cross-tenant import must reach the tenant named in the manifest (the write guard used to reject it)");

        var importedRealm = await db.Realms.IgnoreQueryFilters()
            .SingleOrDefaultAsync(r => r.TenantId == importedTenant!.Id && r.Name == "imported-realm");

        Assert.IsNotNull(importedRealm,
            "the realm of the imported tenant must be stored for that tenant");
    }

    [TestMethod]
    public async Task Import_IntoExistingSecondTenant_IsNotRejectedByTheWriteGuard()
    {
        using var factory = CreateFactory();
        await SeedSecondTenantAsync(factory);
        using var client = CreateClient(factory);

        var manifest = new ExportManifest
        {
            Version = 1,
            ExportType = "tenant",
            ExportMode = "obfuscated",
            Data = new SeedManifest
            {
                Version = 1,
                Tenants =
                [
                    new TenantSeedDefinition
                    {
                        Slug = SecondTenantSlug,
                        Name = "Acme Corp Renamed"
                    }
                ]
            }
        };

        var response = await PostImportAsync(client, factory, manifest);

        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            $"an import into an existing non-default tenant must not be rejected by the tenant write guard: {body}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        using var crossTenant = TenantFilterScope.BeginCrossTenantScope();

        var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Slug == SecondTenantSlug);
        Assert.AreEqual("Acme Corp Renamed", tenant.Name);
    }

    [TestMethod]
    public async Task Import_WithoutTheAntiforgeryToken_IsRejected()
    {
        // M-CSRF: the admin API is antiforgery protected for cookie-authenticated clients.
        using var factory = CreateFactory();
        await SeedSecondTenantAsync(factory);
        using var client = CreateClient(factory);

        var manifest = new ExportManifest
        {
            Version = 1,
            ExportType = "tenant",
            ExportMode = "obfuscated",
            Data = new SeedManifest
            {
                Version = 1,
                Tenants = [new TenantSeedDefinition { Slug = "csrf-target", Name = "CSRF Target" }]
            }
        };

        var response = await client.PostAsJsonAsync("/admin/api/platform/tenants/import", new
        {
            manifest = JsonSerializer.Serialize(manifest, JsonOptions),
            defaultConflictResolution = "Merge"
        });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
            "a cookie-authenticated write without the antiforgery token must be rejected");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        using var crossTenant = TenantFilterScope.BeginCrossTenantScope();
        Assert.AreEqual(0, await db.Tenants.IgnoreQueryFilters().CountAsync(t => t.Slug == "csrf-target"),
            "the rejected request must not have written anything");
    }

    [TestMethod]
    public async Task SystemScope_Alone_DoesNotLiftTheTenantWriteGuard()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var accessor = new TenantAccessor();
        accessor.SetTenant(new TenantContext { TenantId = Guid.NewGuid(), Slug = "a", Name = "A" });
        await using var db = new AuthDbContext(options, accessor);

        db.Tenants.Add(new Tenant { Slug = "other", Name = "Other", Status = TenantStatus.Active });
        await db.SaveChangesAsync();

        db.Realms.Add(new Realm { TenantId = Guid.NewGuid(), Name = "cross" });

        using (TenantFilterScope.BeginSystemScope())
        {
            var threw = false;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.IsTrue(threw,
                "a cross-tenant READ scope must not silently gain a cross-tenant WRITE");
        }
    }

    [TestMethod]
    public async Task CrossTenantScope_LiftsTheTenantWriteGuard()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var accessor = new TenantAccessor();
        accessor.SetTenant(new TenantContext { TenantId = Guid.NewGuid(), Slug = "a", Name = "A" });
        await using var db = new AuthDbContext(options, accessor);

        var otherTenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = otherTenantId, Slug = "other", Name = "Other", Status = TenantStatus.Active });
        await db.SaveChangesAsync();

        db.Realms.Add(new Realm { TenantId = otherTenantId, Name = "cross" });

        using (TenantFilterScope.BeginCrossTenantScope())
        {
            await db.SaveChangesAsync();
        }

        Assert.IsFalse(TenantFilterScope.AllowsCrossTenantWrites,
            "the cross-tenant scope must end with the request");
        Assert.AreEqual(1, await db.Realms.IgnoreQueryFilters().CountAsync(r => r.TenantId == otherTenantId));
    }

    /// <summary>
    /// Posts an import manifest the way a browser does: with the antiforgery cookie and token.
    /// </summary>
    private static Task<HttpResponseMessage> PostImportAsync(
        HttpClient client,
        WebApplicationFactory<Program> factory,
        ExportManifest manifest)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/platform/tenants/import")
        {
            Content = JsonContent.Create(new
            {
                manifest = JsonSerializer.Serialize(manifest, JsonOptions),
                defaultConflictResolution = "Merge"
            })
        };

        MrWhoOidc.UnitTests.Helpers.TestAntiforgeryHelper.AddToken(client, request, factory.Services, TestUserId, TestUserName);
        return client.SendAsync(request);
    }

    private static async Task<ExportManifest> ReadManifestAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        var manifest = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);
        Assert.IsNotNull(manifest, $"the export body must be a manifest: {json}");
        return manifest!;
    }

    private sealed class ScopeTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "ScopeTest";
        public static bool IsPlatformAdmin;

        public ScopeTestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // A user id that does not exist in the database: the tenant membership middleware leaves the
            // principal alone when it cannot resolve an owning tenant.
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, TestUserId),
                new Claim(ClaimTypes.Name, TestUserName)
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private sealed class ScopeAllowAllAdminHandler : AuthorizationHandler<AdminRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class ScopeAllowAllTenantAdminHandler : AuthorizationHandler<TenantAdminRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantAdminRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class ScopePlatformAdminHandler : AuthorizationHandler<PlatformAdminRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformAdminRequirement requirement)
        {
            if (ScopeTestAuthHandler.IsPlatformAdmin)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
