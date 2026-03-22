using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Handlers.External;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class ExternalOidcAutoApprovalAssignmentTests
{
    [TestMethod]
    public async Task ProvisionOrLinkUser_AutoApproval_AssignsUserToClient_EvenWhenAutoAssignDisabled()
    {
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var clientPublicId = "web";
        var realmId = Guid.Parse("00000000-0000-0000-0000-000000000002");

        var (scope, _, ctx) = ExternalOidcTestHost.Create(
            configureServices: services =>
            {
                services.AddSingleton<IOptions<OidcOptions>>(Options.Create(new OidcOptions { Issuer = "https://localhost" }));

                // Use a DB-backed client store so the provisioner and registration service see the same client record.
                services.AddScoped<IClientStore, DbBackedClientStore>();

                // Register real domain services needed for auto-approval
                services.AddScoped<MrWhoOidc.Auth.Services.Users.IRegistrationService, MrWhoOidc.Auth.Services.Users.RegistrationService>();
                services.AddScoped<IIssuerBuilder, MrWhoOidc.Auth.MultiTenancy.IssuerBuilder>();

                // IssuerBuilder depends on IMultiTenancyOptions
                var mtProvider = new MultiTenancyStateProvider("default", initialEnabled: false);
                services.AddSingleton<IMultiTenancyOptions>(mtProvider);
            },
            configureContext: http =>
            {
                http.Request.Scheme = "https";
                http.Request.Host = new Microsoft.AspNetCore.Http.HostString("test.example.com");
            },
            inMemoryDbName: "ext-auto-approval-assign-" + Guid.NewGuid().ToString("N"),
            useEphemeralDataProtectionProvider: true,
            useRecordingMetrics: false);

        using (scope)
        {
            var sp = ctx.RequestServices;
            var db = sp.GetRequiredService<AuthDbContext>();

            // Ensure realm exists (so realmId references are sane in tests, even though InMemory doesn't enforce FK).
            db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "default" });

            db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
            {
                TenantId = tenantId,
                ClientId = clientPublicId,
                ClientName = "Web",
                RealmId = realmId,
                AllowLocalLogin = false,
                AllowExternalIdp = true,
                AllowExternalAutoProvision = true,
                AutoApprovalMode = AutoApprovalMode.OnlyExternalIdp,
                AutoAssignNewUsersToClient = false
            });
            await db.SaveChangesAsync();

            var provisioner = sp.GetRequiredService<IExternalOidcUserProvisioner>();

            var result = await provisioner.ProvisionOrLinkUserAsync(
                provider: "up1",
                issuer: "https://issuer.example.com",
                subject: "sub-123",
                email: "newuser@example.com",
                name: "New User",
                returnUrl: "/authorize?client_id=web",
                clientId: clientPublicId,
                correlationId: "corr",
                correlationHandle: null,
                mappedClaims: new Dictionary<string, string>(),
                cancellationToken: default);

            Assert.IsTrue(result.Success, "Expected provisioning to succeed");
            Assert.AreEqual("auto_approved", result.Outcome, "Expected auto-approved provisioning path");
            Assert.IsTrue(result.UserId.HasValue, "Expected UserId to be returned");

            var assigned = await db.UserClientAssignments.AsNoTracking().AnyAsync(a =>
                a.UserId == result.UserId!.Value &&
                a.ClientId == db.Clients.Single(c => c.ClientId == clientPublicId).Id &&
                a.RealmId == realmId &&
                a.IsActive);

            Assert.IsTrue(assigned, "Expected auto-approved user to be assigned to the client");
        }
    }

    [TestMethod]
    public async Task ProvisionOrLinkUser_ExistingExternalIdentity_BackfillsMissingClientAssignment_WhenAutoApprovalEnabled()
    {
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var clientPublicId = "web";
        var realmId = Guid.Parse("00000000-0000-0000-0000-000000000002");

        var (scope, _, ctx) = ExternalOidcTestHost.Create(
            configureServices: services =>
            {
                services.AddSingleton<IOptions<OidcOptions>>(Options.Create(new OidcOptions { Issuer = "https://localhost" }));
                services.AddScoped<IClientStore, DbBackedClientStore>();
            },
            configureContext: http =>
            {
                http.Request.Scheme = "https";
                http.Request.Host = new Microsoft.AspNetCore.Http.HostString("test.example.com");
            },
            inMemoryDbName: "ext-auto-approval-backfill-" + Guid.NewGuid().ToString("N"),
            useEphemeralDataProtectionProvider: true,
            useRecordingMetrics: false);

        using (scope)
        {
            var sp = ctx.RequestServices;
            var db = sp.GetRequiredService<AuthDbContext>();

            db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "default" });

            var client = new MrWhoOidc.Auth.Persistence.Client
            {
                TenantId = tenantId,
                ClientId = clientPublicId,
                ClientName = "Web",
                RealmId = realmId,
                AllowLocalLogin = false,
                AllowExternalIdp = true,
                AllowExternalAutoProvision = true,
                AutoApprovalMode = AutoApprovalMode.OnlyExternalIdp,
                AutoAssignNewUsersToClient = false
            };
            db.Clients.Add(client);

            var user = new User
            {
                TenantId = tenantId,
                Username = "existing@example.com",
                Email = "existing@example.com",
                Name = "Existing"
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            // Existing external identity link, but no client assignment yet.
            db.ExternalIdentities.Add(new ExternalIdentity
            {
                Issuer = "https://issuer.example.com",
                Subject = "sub-123",
                UserId = user.Id,
                ProviderName = "up1",
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            var provisioner = sp.GetRequiredService<IExternalOidcUserProvisioner>();

            var result = await provisioner.ProvisionOrLinkUserAsync(
                provider: "up1",
                issuer: "https://issuer.example.com",
                subject: "sub-123",
                email: "existing@example.com",
                name: "Existing",
                returnUrl: "/authorize?client_id=web",
                clientId: clientPublicId,
                correlationId: "corr",
                correlationHandle: null,
                mappedClaims: new Dictionary<string, string>(),
                cancellationToken: default);

            Assert.IsTrue(result.Success, "Expected provisioning to succeed");
            Assert.AreEqual("linked", result.Outcome, "Expected linked path");
            Assert.AreEqual(user.Id, result.UserId, "Expected existing user id");

            var assigned = await db.UserClientAssignments.AsNoTracking().AnyAsync(a =>
                a.UserId == user.Id &&
                a.ClientId == client.Id &&
                a.RealmId == realmId &&
                a.IsActive);

            Assert.IsTrue(assigned, "Expected existing user to be assigned to the client after external login");
        }
    }

    private sealed class DbBackedClientStore : IClientStore
    {
        private readonly AuthDbContext _db;

        public DbBackedClientStore(AuthDbContext db) => _db = db;

        public Task<MrWhoOidc.Auth.Persistence.Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
            => _db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId, ct);

        public Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default)
            => Task.FromResult(false);

        public IQueryable<MrWhoOidc.Auth.Persistence.Client> QueryClients(CancellationToken ct = default)
            => _db.Clients.AsQueryable();

        public Task InvalidateClientCacheAsync(string clientId, Guid tenantId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<MrWhoOidc.Auth.Persistence.ClientSecret?> GetPrimarySecretAsync(Guid clientRecordId, CancellationToken ct = default)
            => Task.FromResult<MrWhoOidc.Auth.Persistence.ClientSecret?>(null);

        public Task<List<MrWhoOidc.Auth.Persistence.ClientSecret>> GetActiveSecretsAsync(Guid clientRecordId, CancellationToken ct = default)
            => Task.FromResult(new List<MrWhoOidc.Auth.Persistence.ClientSecret>());

        public Task<MrWhoOidc.Auth.Persistence.ClientSecret> CreateSecretAsync(
            Guid clientRecordId,
            string secretValue,
            string? description,
            string? createdBy,
            DateTime? expiresAtUtc = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ActivateSecretAsync(Guid secretId, string activatedBy, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> SetPrimarySecretAsync(Guid secretId, string updatedBy, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> RevokeSecretAsync(Guid secretId, string revokedBy, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> RecordSecretUsageAsync(Guid secretId, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
