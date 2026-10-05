using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Wave 2: the upstream client secret inside IdentityProvider.ConfigJson was stored in plaintext. It is now protected
/// member-wise at rest, and every reader of the entity still gets the plaintext config.
/// </summary>
[TestClass]
public sealed class ProviderConfigSecretProtectionTests
{
    private const string Config = "{\"Authority\":\"https://idp.example\",\"ClientId\":\"rp\",\"ClientSecret\":\"s3cr3t\",\"Note\":\"Žluťoučký kůň\"}";

    [TestMethod]
    public async Task ClientSecret_IsProtectedAtRest_AndPlaintextForReaders()
    {
        using var fixture = new Fixture();
        var provider = new IdentityProvider { Name = "entra", ConfigJson = Config };
        await using (var db = fixture.Db())
        {
            db.IdentityProviders.Add(provider);
            await db.SaveChangesAsync();

            Assert.AreEqual(Config, provider.ConfigJson, "the tracked entity keeps the plaintext config after saving");
            Assert.AreEqual(EntityState.Unchanged, db.Entry(provider).State);
            Assert.IsFalse(db.ChangeTracker.HasChanges());
        }

        await using (var raw = fixture.RawDb())
        {
            var stored = (await raw.IdentityProviders.SingleAsync()).ConfigJson!;
            Assert.IsFalse(stored.Contains("s3cr3t", StringComparison.Ordinal), stored);
            Assert.IsTrue(stored.Contains("\"ClientSecret\":\"dp:v1:", StringComparison.Ordinal), stored);
            Assert.IsTrue(stored.Contains("Žluťoučký kůň", StringComparison.Ordinal), "other members and diacritics are untouched");
        }

        await using (var db = fixture.Db())
        {
            var noTracking = await db.IdentityProviders.AsNoTracking().SingleAsync();
            Assert.IsTrue(OidcProviderConfig.TryParse(noTracking.ConfigJson!, out var cfg).ok);
            Assert.AreEqual("s3cr3t", cfg!.ClientSecret);

            var tracked = await db.IdentityProviders.SingleAsync();
            Assert.AreEqual(Config, tracked.ConfigJson);
            Assert.IsFalse(db.ChangeTracker.HasChanges(), "loading a provider must not dirty it");

            tracked.DisplayName = "Entra";
            await db.SaveChangesAsync();
        }

        await using (var raw = fixture.RawDb())
        {
            var stored = (await raw.IdentityProviders.SingleAsync()).ConfigJson!;
            Assert.IsFalse(stored.Contains("s3cr3t", StringComparison.Ordinal), "an unrelated edit must not write the secret back in plaintext");
        }
    }

    [TestMethod]
    public async Task Backfill_ProtectsLegacyPlaintextConfigs_Once()
    {
        using var fixture = new Fixture();
        await using (var raw = fixture.RawDb())
        {
            raw.IdentityProviders.AddRange(
                new IdentityProvider { Name = "legacy", ConfigJson = "{\"Authority\":\"https://a\",\"ClientId\":\"rp\",\"client_secret\":\"old\"}" },
                new IdentityProvider { Name = "public", ConfigJson = "{\"Authority\":\"https://b\",\"ClientId\":\"rp\"}" });
            await raw.SaveChangesAsync();
        }

        await using (var db = fixture.Db())
        {
            Assert.AreEqual(1, await ProviderConfigSecretProtectionBackfill.RunAsync(db, NullLogger.Instance));
            Assert.AreEqual(0, await ProviderConfigSecretProtectionBackfill.RunAsync(db, NullLogger.Instance), "idempotent");
        }

        await using (var raw = fixture.RawDb())
        {
            var legacy = (await raw.IdentityProviders.SingleAsync(p => p.Name == "legacy")).ConfigJson!;
            Assert.IsTrue(legacy.Contains("\"client_secret\":\"dp:v1:", StringComparison.Ordinal), legacy);
        }

        await using (var db = fixture.Db())
        {
            var legacy = await db.IdentityProviders.AsNoTracking().SingleAsync(p => p.Name == "legacy");
            Assert.IsTrue(legacy.ConfigJson!.Contains("\"client_secret\":\"old\"", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void AdminViews_RedactTheSecret_AndAnEchoedMarkerKeepsTheStoredOne()
    {
        var redacted = ProviderConfigSecrets.Redact(Config)!;
        Assert.IsFalse(redacted.Contains("s3cr3t", StringComparison.Ordinal));
        StringAssert.Contains(redacted, ProviderConfigSecrets.RedactedMarker);

        var echoed = ProviderConfigSecrets.RestoreRedacted(redacted.Replace("https://idp.example", "https://idp2.example"), Config)!;
        Assert.IsTrue(OidcProviderConfig.TryParse(echoed, out var cfg).ok);
        Assert.AreEqual("s3cr3t", cfg!.ClientSecret);
        Assert.AreEqual("https://idp2.example", cfg.Authority);

        var replaced = ProviderConfigSecrets.RestoreRedacted(Config.Replace("s3cr3t", "new"), Config)!;
        Assert.IsTrue(OidcProviderConfig.TryParse(replaced, out var cfg2).ok);
        Assert.AreEqual("new", cfg2!.ClientSecret, "a new secret replaces the stored one");
    }

    [TestMethod]
    public async Task FullExport_StillCarriesThePlaintextSecret_ObfuscatedDoesNot()
    {
        using var fixture = new Fixture();
        var tenantId = Guid.NewGuid();
        var provider = new IdentityProvider { Name = "entra", TenantId = tenantId, ConfigJson = Config };
        await using (var db = fixture.Db())
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Slug = "acme", Name = "Acme", IssuerUri = "https://idp/t/acme" });
            db.IdentityProviders.Add(provider);
            await db.SaveChangesAsync();
        }

        await using var exportDb = fixture.Db();
        var export = new MrWhoOidc.WebAuth.Services.ConfigurationExportService(exportDb, NullLogger<MrWhoOidc.WebAuth.Services.ConfigurationExportService>.Instance, fixture.Protector);
        var full = await export.ExportIdentityProviderAsync(provider.Id, new MrWhoOidc.Auth.Seeding.ExportOptions { Mode = MrWhoOidc.Auth.Seeding.ExportMode.Full });
        var obfuscated = await export.ExportIdentityProviderAsync(provider.Id, new MrWhoOidc.Auth.Seeding.ExportOptions { Mode = MrWhoOidc.Auth.Seeding.ExportMode.Obfuscated });

        Assert.AreEqual("s3cr3t", full.Data.IdentityProviders!.Single().Config!["ClientSecret"]?.ToString());
        Assert.AreNotEqual("s3cr3t", obfuscated.Data.IdentityProviders!.Single().Config!["ClientSecret"]?.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mrwho-oidc-dp-" + Guid.NewGuid().ToString("N"));
        private readonly DbContextOptions<AuthDbContext> _options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Protector = new DataProtectionSecretProtector(DataProtectionProvider.Create(new DirectoryInfo(_directory)));
        }

        public ISecretProtector Protector { get; }

        public AuthDbContext Db() => new(_options, null, Protector);

        /// <summary>No protector: sees the stored values as they are.</summary>
        public AuthDbContext RawDb() => new(_options, null, null);

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }
}
