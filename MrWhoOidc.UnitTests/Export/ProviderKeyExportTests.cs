using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Export;

/// <summary>
/// Third 2026-10-04 review: provider export copied IdentityProviderKey.Jwk verbatim in every mode. Those keys are the
/// private keys MrWhoOidc signs upstream request objects with, so an obfuscated export (or a read-only support
/// session) handed out a credential for the upstream IdP.
/// </summary>
[TestClass]
public sealed class ProviderKeyExportTests
{
    private static async Task<string> ExportedJwkAsync(ExportMode mode)
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenant = new Tenant { Slug = "acme", Name = "Acme", IssuerUri = "https://idp/t/acme" };
        var provider = new IdentityProvider { Name = "entra", TenantId = tenant.Id };
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateJwk = JsonSerializer.Serialize(JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(ec)));
        Assert.IsTrue(privateJwk.Contains("\"d\""), "test setup: the stored key must be private");
        db.AddRange(tenant, provider, new IdentityProviderKey { IdentityProviderId = provider.Id, Alg = "ES256", Kid = "k1", Jwk = privateJwk });
        await db.SaveChangesAsync();

        var manifest = await new ConfigurationExportService(db, NullLogger<ConfigurationExportService>.Instance)
            .ExportIdentityProviderAsync(provider.Id, new ExportOptions { Mode = mode });

        return manifest.Data.IdentityProviders!.Single().Keys.Single().Jwk!;
    }

    [TestMethod]
    public async Task ObfuscatedExport_CarriesOnlyThePublicKey()
    {
        using var doc = JsonDocument.Parse(await ExportedJwkAsync(ExportMode.Obfuscated));

        Assert.IsFalse(doc.RootElement.TryGetProperty("d", out _), "the private scalar must not be exported");
        Assert.IsTrue(doc.RootElement.TryGetProperty("x", out _) && doc.RootElement.TryGetProperty("y", out _), "the public key stays");
    }

    [TestMethod]
    public async Task FullExport_KeepsThePrivateKeyForMigration()
    {
        using var doc = JsonDocument.Parse(await ExportedJwkAsync(ExportMode.Full));

        Assert.IsTrue(doc.RootElement.TryGetProperty("d", out _));
    }

    [TestMethod]
    [DataRow("-----BEGIN PRIVATE KEY-----")]
    [DataRow("[1,2]")]
    public void ToPublicJwk_UnrecognisedInput_ExportsNothing(string input)
        => Assert.AreEqual(string.Empty, ConfigurationExportService.ToPublicJwk(input));
}
