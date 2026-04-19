using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class BootstrapManifestApplyEndpointTests
{
    private const string BootstrapToken = "integration-bootstrap-token";

    private static WebApplicationFactory<Program> CreateFactoryWithManifest(string manifestJson)
        => ((WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory()).WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddTestInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Bootstrap:Token"] = BootstrapToken,
                    ["Auth:EnableDynamicClientRegistration"] = "true",
                    ["Seeding:ManifestJson"] = manifestJson,
                    ["Seeding:AllowUpdates"] = "true"
                });
            });
        });

    private static string CreateManifestJson()
    {
        var manifest = new SeedManifest
        {
            PlatformSettings = new PlatformSettingsSeedDefinition
            {
                DynamicClientRegistrationEnabled = true
            },
            PlatformInitialAccessTokens =
            [
                new PlatformInitialAccessTokenSeedDefinition
                {
                    Token = "oidf-test-initial-access-token",
                    Description = "OIDF integration test token"
                }
            ],
            Tenants =
            [
                new TenantSeedDefinition
                {
                    Slug = "default",
                    Name = "Default Tenant",
                    DynamicClientRegistrationRealm = "default",
                    Realms =
                    [
                        new RealmSeedDefinition
                        {
                            Name = "default",
                            DisplayName = "Default"
                        },
                        new RealmSeedDefinition
                        {
                            Name = "admin",
                            DisplayName = "Admin"
                        }
                    ],
                    Users =
                    [
                        new UserSeedDefinition
                        {
                            Username = "oidf-cert-user",
                            Email = "oidf-cert-user@mrwho.local",
                            Password = "OidfCertUser123!",
                            EmailVerified = true,
                            Clients =
                            [
                                new UserClientSeedAssignment
                                {
                                    ClientId = "oidf-basic-primary",
                                    Realm = "default"
                                }
                            ]
                        }
                    ],
                    Clients =
                    [
                        new ClientSeedDefinition
                        {
                            ClientId = "oidf-basic-primary",
                            ClientName = "OIDF Basic Primary",
                            Realm = "default",
                            RequirePkce = false,
                            RequireConsent = false,
                            AllowLocalLogin = true,
                            AllowExternalIdp = false,
                            ClientSecret = "oidf-basic-primary-dev-secret",
                            AllowedLoginRedirectUris = ["https://suite.example.test/callback"],
                            AllowedLogoutRedirectUris = ["https://suite.example.test/post_logout_redirect"],
                            AllowedScopes = ["openid", "profile", "email"]
                        }
                    ]
                }
            ]
        };

        return JsonSerializer.Serialize(manifest);
    }

    [TestMethod]
    public async Task ApplySeedManifest_ReturnsUnauthorized_WhenTokenIsInvalid()
    {
        var manifestJson = CreateManifestJson();
        using var factory = CreateFactoryWithManifest(manifestJson);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bootstrap/apply-seed-manifest");
        request.Headers.Add("X-Bootstrap-Token", "wrong-token");

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ApplySeedManifest_SeedsCertificationClient_And_EnablesDiscoveryRegistrationEndpoint()
    {
        var manifestJson = CreateManifestJson();
        using var factory = CreateFactoryWithManifest(manifestJson);
        var multiTenancyState = factory.Services.GetRequiredService<IMultiTenancyStateProvider>();
        multiTenancyState.UpdateState(false);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bootstrap/apply-seed-manifest");
        request.Headers.Add("X-Bootstrap-Token", BootstrapToken);

        var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, payload);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var platformSettingsService = scope.ServiceProvider.GetRequiredService<IPlatformSettingsService>();

            var seededClient = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == "oidf-basic-primary");
            var seededUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == "oidf-cert-user");
            var seededAccount = await db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Username == "oidf-cert-user");
            Assert.IsNotNull(seededClient, "certification client should be created");
            Assert.IsNotNull(seededUser, "certification user should be created");
            Assert.IsNotNull(seededAccount, "certification user account should be created");
            Assert.IsFalse(string.IsNullOrWhiteSpace(seededAccount.PasswordHash), "certification user password hash should be stored");
            Assert.AreEqual(EmailNormalizer.NormalizeForLookup("oidf-cert-user@mrwho.local"), seededAccount.NormalizedEmail, "certification user email should be normalized");
            Assert.IsTrue(await db.UserClientAssignments.AnyAsync(a => a.UserId == seededUser.Id && a.ClientId == seededClient.Id && a.IsActive), "certification user should be assigned to the fallback client");
            Assert.IsTrue(await db.PlatformInitialAccessTokens.AnyAsync(), "initial access token should be stored");

            var platformSettings = await platformSettingsService.GetSettingsAsync();
            Assert.IsTrue(platformSettings.DynamicClientRegistrationEnabled, "platform DCR should be enabled");
        }

        var discoveryResponse = await client.GetAsync("/.well-known/openid-configuration");
        var discoveryJson = await discoveryResponse.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, discoveryResponse.StatusCode, discoveryJson);

        using var document = JsonDocument.Parse(discoveryJson);
        Assert.IsTrue(document.RootElement.TryGetProperty("registration_endpoint", out var registrationEndpoint), "registration_endpoint should be advertised after manifest apply");
        StringAssert.Contains(registrationEndpoint.GetString() ?? string.Empty, "/register");
    }
}