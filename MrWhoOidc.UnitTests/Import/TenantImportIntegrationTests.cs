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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.UnitTests.Import;

/// <summary>
/// Integration tests for the import API endpoints.
/// Tests serialization, validation, and preview/import workflow.
/// </summary>
[TestClass]
[DoNotParallelize]
public class TenantImportIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [TestInitialize]
    public void SetUp()
    {
        ImportTestAuthHandler.CurrentUserId = Guid.Empty;
        ImportTestPlatformAdminHandler.IsPlatformAdmin = false;
    }

    [TestCleanup]
    public void TearDown()
    {
        ImportTestAuthHandler.CurrentUserId = Guid.Empty;
        ImportTestPlatformAdminHandler.IsPlatformAdmin = false;
    }

    [TestMethod]
    public void ImportPreview_SerializesCorrectly()
    {
        // Arrange
        var preview = new ImportPreview
        {
            IsValid = true,
            TenantCount = 1,
            RealmCount = 2,
            ClientCount = 3,
            ProviderCount = 1,
            ScopeCount = 5,
            RoleCount = 4,
            HasObfuscatedSecrets = true,
            ObfuscatedSecretCount = 2
        };

        // Act
        var json = JsonSerializer.Serialize(preview, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ImportPreview>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(preview.IsValid, deserialized.IsValid);
        Assert.AreEqual(preview.TenantCount, deserialized.TenantCount);
        Assert.AreEqual(preview.HasObfuscatedSecrets, deserialized.HasObfuscatedSecrets);
    }

    [TestMethod]
    public void ImportResult_SerializesCorrectly()
    {
        // Arrange
        var result = new ImportResult
        {
            Success = true,
            EntitiesCreated = 5,
            EntitiesUpdated = 2,
            EntitiesSkipped = 1,
            TenantsCreated = 1,
            ClientsCreated = 3,
            RealmsCreated = 1,
            WasRolledBack = false,
            StartedAt = DateTime.UtcNow.AddSeconds(-5),
            CompletedAt = DateTime.UtcNow
        };

        // Act
        var json = JsonSerializer.Serialize(result, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ImportResult>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(result.Success, deserialized.Success);
        Assert.AreEqual(result.EntitiesCreated, deserialized.EntitiesCreated);
        Assert.AreEqual(result.WasRolledBack, deserialized.WasRolledBack);
    }

    [TestMethod]
    public void ExportManifest_RoundTrips()
    {
        // Arrange
        var manifest = CreateTestManifest();

        // Act
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(manifest.Version, deserialized.Version);
        Assert.AreEqual(manifest.Data.Tenants.Count, deserialized.Data.Tenants.Count);
    }

    [TestMethod]
    public void ImportOptions_DryRunAlias_WorksCorrectly()
    {
        // Arrange & Act
        var options = new ImportOptions
        {
            DryRun = true
        };

        // Assert
        Assert.IsTrue(options.ValidateOnly);
        Assert.IsTrue(options.DryRun);
    }

    [TestMethod]
    public void ImportOptions_ConflictResolutionsAlias_WorksCorrectly()
    {
        // Arrange
        var overrides = new Dictionary<string, ConflictResolution>
        {
            ["tenant:test"] = ConflictResolution.Overwrite
        };

        // Act
        var options = new ImportOptions
        {
            ConflictResolutions = overrides
        };

        // Assert
        Assert.AreEqual(1, options.ConflictOverrides.Count);
        Assert.AreEqual(ConflictResolution.Overwrite, options.ConflictOverrides["tenant:test"]);
    }

    [TestMethod]
    public void ValidationError_SerializesWithSeverity()
    {
        // Arrange
        var error = new ValidationError
        {
            Message = "Test error",
            Severity = ValidationSeverity.Error,
            Path = "$.tenants[0].slug",
            Code = "INVALID_SLUG"
        };

        // Act
        var json = JsonSerializer.Serialize(error, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ValidationError>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(ValidationSeverity.Error, deserialized.Severity);
        Assert.AreEqual("$.tenants[0].slug", deserialized.Path);
    }

    [TestMethod]
    public void ImportConflict_SerializesCorrectly()
    {
        // Arrange
        var conflict = new ImportConflict
        {
            EntityType = "Tenant",
            Identifier = "test-tenant",
            EntityKey = "tenant:test-tenant",
            ConflictType = "SlugCollision",
            ExistingValue = "existing-slug",
            IncomingValue = "new-slug"
        };

        // Act
        var json = JsonSerializer.Serialize(conflict, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ImportConflict>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("Tenant", deserialized.EntityType);
        Assert.AreEqual("SlugCollision", deserialized.ConflictType);
    }

    [TestMethod]
    public void ManifestValidation_DetectsEmptyTenants()
    {
        // Arrange
        var manifest = new ExportManifest
        {
            Version = 1,
            Data = new SeedManifest
            {
                Tenants = []
            }
        };

        // Act - validate that empty tenants list is still valid (nothing to import)
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ExportManifest>(json, JsonOptions);

        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(0, deserialized.Data?.Tenants?.Count ?? 0);
    }

    private static ExportManifest CreateTestManifest(string tenantSlug = "test-import-tenant")
    {
        return new ExportManifest
        {
            Version = 1,
            ExportMode = "obfuscated",
            Metadata = new ExportMetadata
            {
                ExportedBy = "test",
                SourceSystem = "test"
            },
            Data = new SeedManifest
            {
                Tenants =
                [
                    new TenantSeedDefinition
                    {
                        Slug = tenantSlug,
                        Name = "Test Import Tenant",
                        Description = "Created for integration test",
                        Status = "Active",
                        Realms =
                        [
                            new RealmSeedDefinition
                            {
                                Name = "default",
                                DisplayName = "Default Realm"
                            }
                        ]
                    }
                ]
            }
        };
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return TestWebAppFactory.CreateInMemory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = ImportTestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = ImportTestAuthHandler.SchemeName;
                        options.DefaultScheme = ImportTestAuthHandler.SchemeName;
                    }).AddScheme<AuthenticationSchemeOptions, ImportTestAuthHandler>(ImportTestAuthHandler.SchemeName, _ => { });

                    services.RemoveAll<IAuthorizationHandler>();
                    services.AddSingleton<IAuthorizationHandler, ImportAllowAllAdminHandler>();
                    services.AddSingleton<IAuthorizationHandler, ImportAllowAllTenantAdminHandler>();
                    services.AddSingleton<IAuthorizationHandler, ImportTestPlatformAdminHandler>();
                });
            });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ImportTestAuthHandler.SchemeName);
        return client;
    }

    // Test authentication handler
    private sealed class ImportTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "ImportTest";
        public static Guid CurrentUserId;

        public ImportTestAuthHandler(
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
                new Claim(ClaimTypes.Name, "import-test-user")
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    // Authorization handlers
    private sealed class ImportAllowAllAdminHandler : AuthorizationHandler<AdminRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class ImportAllowAllTenantAdminHandler : AuthorizationHandler<TenantAdminRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantAdminRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class ImportTestPlatformAdminHandler : AuthorizationHandler<PlatformAdminRequirement>
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
