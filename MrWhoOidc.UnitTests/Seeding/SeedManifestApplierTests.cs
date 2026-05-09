using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Seeding;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Seeding;

[TestClass]
public class SeedManifestApplierTests
{
    private AuthDbContext _db = null!;
    private Mock<ITenantAccessor> _tenantAccessor = null!;
    private Mock<IMultiTenancyOptions> _multiTenancyOptions = null!;
    private Mock<IIssuerBuilder> _issuerBuilder = null!;
    private Mock<IOptions<OidcOptions>> _oidcOptions = null!;
    private Mock<IOptions<SeedManifestOptions>> _seedOptions = null!;
    private Mock<IConfiguration> _configuration = null!;
    private Mock<IPasswordHasher> _passwordHasher = null!;
    private Mock<IClientStore> _clientStore = null!;
    private Mock<IPlatformSettingsService> _platformSettingsService = null!;
    private Mock<IUserAccountProvisioner> _accountProvisioner = null!;
    private Mock<ILogger<SeedManifestApplier>> _logger = null!;
    private SeedManifestApplier _applier = null!;

    [TestInitialize]
    public void Setup()
    {
        _db = TestDataSeeder.CreateInMemoryDb();
        _tenantAccessor = new Mock<ITenantAccessor>();
        _multiTenancyOptions = new Mock<IMultiTenancyOptions>();
        _issuerBuilder = new Mock<IIssuerBuilder>();
        _oidcOptions = new Mock<IOptions<OidcOptions>>();
        _seedOptions = new Mock<IOptions<SeedManifestOptions>>();
        _configuration = new Mock<IConfiguration>();
        _passwordHasher = new Mock<IPasswordHasher>();
        _clientStore = new Mock<IClientStore>();
        _platformSettingsService = new Mock<IPlatformSettingsService>();
        _accountProvisioner = new Mock<IUserAccountProvisioner>();
        _logger = new Mock<ILogger<SeedManifestApplier>>();

        _oidcOptions.Setup(o => o.Value).Returns(new OidcOptions());
        _seedOptions.Setup(o => o.Value).Returns(new SeedManifestOptions());
        _platformSettingsService.Setup(service => service.GetSettingsAsync()).ReturnsAsync(new PlatformSettings());
        _platformSettingsService
            .Setup(service => service.UpdateSettingsAsync(It.IsAny<PlatformSettings>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        _clientStore
            .Setup(store => store.InvalidateClientCacheAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _accountProvisioner
            .Setup(service => service.EnsureAsync(It.IsAny<User>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        _applier = new SeedManifestApplier(
            _db,
            _tenantAccessor.Object,
            _multiTenancyOptions.Object,
            _issuerBuilder.Object,
            _oidcOptions.Object,
            _seedOptions.Object,
            _configuration.Object,
            _passwordHasher.Object,
            _clientStore.Object,
            _platformSettingsService.Object,
            _accountProvisioner.Object,
            _logger.Object
        );
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    [TestMethod]
    public async Task ApplyLicensesAsync_WhenManifestContainsLicenses_LogsInformationalNoOp()
    {
        var manifest = new SeedManifest
        {
            Licenses = new List<LicenseSeedDefinition>
            {
                new LicenseSeedDefinition
                {
                    LicenseToken = "dummy-token"
                }
            }
        };

        await _applier.ApplyLicensesAsync(manifest);

#pragma warning disable CS8602, CS8620
        _logger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("licensing is no longer applied by WebAuth")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
#pragma warning restore CS8602, CS8620
    }

    [TestMethod]
    public async Task ApplyForCurrentTenantAsync_CreatesClient_WithAutoAssignNewUsersToClientFromManifest()
    {
        var tenantId = Guid.NewGuid();
        _tenantAccessor.SetupGet(accessor => accessor.CurrentTenant).Returns(new TenantContext
        {
            TenantId = tenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://localhost:8443/t/default"
        });

        _db.Realms.Add(new Realm
        {
            TenantId = tenantId,
            Name = "admin",
            DisplayName = "Admin"
        });
        await _db.SaveChangesAsync();

        var manifest = new SeedManifest
        {
            Tenants = new List<TenantSeedDefinition>
            {
                new()
                {
                    Slug = "default",
                    Name = "Default Tenant",
                    Clients = new List<ClientSeedDefinition>
                    {
                        new()
                        {
                            ClientId = "portal-web",
                            ClientName = "Licensing Portal Web",
                            AutoAssignNewUsersToClient = true,
                            Realm = "admin"
                        }
                    }
                }
            }
        };

        await _applier.ApplyForCurrentTenantAsync(manifest);

        var client = _db.Clients.Single(item => item.ClientId == "portal-web" && item.TenantId == tenantId);
        Assert.IsTrue(client.AutoAssignNewUsersToClient);
    }

    [TestMethod]
    public async Task ApplyForCurrentTenantAsync_UpdatesExistingClient_AutoAssignNewUsersToClientWhenAllowed()
    {
        var tenantId = Guid.NewGuid();
        _tenantAccessor.SetupGet(accessor => accessor.CurrentTenant).Returns(new TenantContext
        {
            TenantId = tenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://localhost:8443/t/default"
        });
        _seedOptions.Setup(o => o.Value).Returns(new SeedManifestOptions { AllowUpdates = true });

        var realm = new Realm
        {
            TenantId = tenantId,
            Name = "admin",
            DisplayName = "Admin"
        };
        _db.Realms.Add(realm);

        _db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            TenantId = tenantId,
            ClientId = "portal-web",
            ClientName = "Licensing Portal Web",
            RealmId = realm.Id,
            AutoAssignNewUsersToClient = false
        });
        await _db.SaveChangesAsync();

        var manifest = new SeedManifest
        {
            Tenants = new List<TenantSeedDefinition>
            {
                new()
                {
                    Slug = "default",
                    Name = "Default Tenant",
                    Clients = new List<ClientSeedDefinition>
                    {
                        new()
                        {
                            ClientId = "portal-web",
                            ClientName = "Licensing Portal Web",
                            AutoAssignNewUsersToClient = true,
                            Realm = "admin"
                        }
                    }
                }
            }
        };

        await _applier.ApplyForCurrentTenantAsync(manifest);

        var client = _db.Clients.Single(item => item.ClientId == "portal-web" && item.TenantId == tenantId);
        Assert.IsTrue(client.AutoAssignNewUsersToClient);
    }

    [TestMethod]
    public async Task ApplyTenantsAsync_UpdatesPlatformSettings_WhenPresentInManifest()
    {
        var manifest = new SeedManifest
        {
            PlatformSettings = new PlatformSettingsSeedDefinition
            {
                DynamicClientRegistrationEnabled = true
            }
        };

        await _applier.ApplyTenantsAsync(manifest, "https://localhost:8443");

        _platformSettingsService.Verify(
            service => service.UpdateSettingsAsync(
                It.Is<PlatformSettings>(settings => settings.DynamicClientRegistrationEnabled),
                "seed-manifest"),
            Times.Once);
    }

    [TestMethod]
    public async Task ApplyTenantsAsync_SeedsPlatformInitialAccessTokens_FromManifest()
    {
        var manifest = new SeedManifest
        {
            PlatformInitialAccessTokens = new List<PlatformInitialAccessTokenSeedDefinition>
            {
                new()
                {
                    Token = "oidf-dcr-initial-access-token",
                    Description = "OIDF certification dynamic registration"
                }
            }
        };

        await _applier.ApplyTenantsAsync(manifest, "https://localhost:8443");

        var token = _db.PlatformInitialAccessTokens.Single();
        var expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("oidf-dcr-initial-access-token")));

        Assert.AreEqual(expectedHash, token.TokenHash);
        Assert.AreEqual("OIDF certification dynamic registration", token.Description);
        Assert.IsNull(token.RevokedAt);
    }

    [TestMethod]
    public async Task ApplyForCurrentTenantAsync_SetsDynamicClientRegistrationRealmId_FromManifestRealmName()
    {
        var tenantId = Guid.NewGuid();
        var realm = new Realm
        {
            TenantId = tenantId,
            Name = "default",
            DisplayName = "Default"
        };

        _db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://localhost:8443/t/default",
            Status = TenantStatus.Active
        });
        _db.Realms.Add(realm);
        await _db.SaveChangesAsync();

        _tenantAccessor.SetupGet(accessor => accessor.CurrentTenant).Returns(new TenantContext
        {
            TenantId = tenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://localhost:8443/t/default"
        });

        var manifest = new SeedManifest
        {
            Tenants = new List<TenantSeedDefinition>
            {
                new()
                {
                    Slug = "default",
                    Name = "Default Tenant",
                    DynamicClientRegistrationRealm = "default"
                }
            }
        };

        await _applier.ApplyForCurrentTenantAsync(manifest);

        var tenant = _db.Tenants.Single(item => item.Id == tenantId);
        var settings = JsonSerializer.Deserialize<MrWhoOidc.Auth.Settings.TenantSettings>(tenant.SettingsJson!);

        Assert.IsNotNull(settings);
        Assert.IsNotNull(settings.Auth);
        Assert.AreEqual(realm.Id, settings.Auth.DynamicClientRegistrationRealmId);
    }

    [TestMethod]
    public async Task ApplyForCurrentTenantAsync_PreservesRequirePkceFalse_WhenOverwritingSeededSecret()
    {
        var tenantId = Guid.NewGuid();
        var realm = new Realm
        {
            TenantId = tenantId,
            Name = "admin",
            DisplayName = "Admin"
        };

        _seedOptions.Setup(o => o.Value).Returns(new SeedManifestOptions
        {
            AllowUpdates = true,
            OverwriteClientSecrets = true
        });

        _tenantAccessor.SetupGet(accessor => accessor.CurrentTenant).Returns(new TenantContext
        {
            TenantId = tenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://localhost:8443/t/default"
        });

        _passwordHasher.Setup(hasher => hasher.Hash("seed-secret")).Returns("hashed-seed-secret");
        _clientStore
            .Setup(store => store.CreateSecretAsync(
                It.IsAny<Guid>(),
                "seed-secret",
                "seed",
                "seed",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid clientId, string _, string? _, string? _, DateTime? _, CancellationToken _) => new ClientSecret
            {
                ClientId = clientId,
                SecretHash = "hashed-seed-secret"
            });
        _clientStore
            .Setup(store => store.ActivateSecretAsync(It.IsAny<Guid>(), "seed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _clientStore
            .Setup(store => store.SetPrimarySecretAsync(It.IsAny<Guid>(), "seed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://localhost:8443/t/default",
            Status = TenantStatus.Active
        });
        _db.Realms.Add(realm);

        _db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            TenantId = tenantId,
            ClientId = "oidf-basic-primary",
            ClientName = "OIDF Basic Primary",
            RealmId = realm.Id,
            RequirePkce = false,
            RequireConsent = false,
            ClientSecretHash = "existing-hash"
        });
        await _db.SaveChangesAsync();

        var manifest = new SeedManifest
        {
            Tenants = new List<TenantSeedDefinition>
            {
                new()
                {
                    Slug = "default",
                    Name = "Default Tenant",
                    Clients = new List<ClientSeedDefinition>
                    {
                        new()
                        {
                            ClientId = "oidf-basic-primary",
                            ClientName = "OIDF Basic Primary",
                            Realm = "admin",
                            RequirePkce = false,
                            ClientSecret = "seed-secret"
                        }
                    }
                }
            }
        };

        await _applier.ApplyForCurrentTenantAsync(manifest);

        var client = _db.Clients.Single(item => item.ClientId == "oidf-basic-primary" && item.TenantId == tenantId);
        Assert.IsFalse(client.RequirePkce);
        Assert.AreEqual("existing-hash", client.ClientSecretHash);
        _clientStore.Verify(
            store => store.CreateSecretAsync(
                client.Id,
                "seed-secret",
                "seed",
                "seed",
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
        _clientStore.Verify(store => store.ActivateSecretAsync(It.IsAny<Guid>(), "seed", It.IsAny<CancellationToken>()), Times.Once);
        _clientStore.Verify(store => store.SetPrimarySecretAsync(It.IsAny<Guid>(), "seed", It.IsAny<CancellationToken>()), Times.Once);
    }
}
