using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Pages.Admin.ProviderKeys;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class ProviderKeysPageTests
{
    static AuthDbContext NewDb(string name)
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AuthDbContext(opts);
    }

    static Guid SeedProvider(AuthDbContext db)
    {
        var id = Guid.NewGuid();
        db.IdentityProviders.Add(new IdentityProvider
        {
            Id = id,
            Name = "test",
            DisplayName = "Test",
            Type = IdentityProviderType.Oidc,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ConfigJson = "{\"Authority\":\"https://idp.example\",\"ClientId\":\"cid\"}"
        });
        db.SaveChanges();
        return id;
    }

    static string RsaPkcs8Pem()
    {
        using var rsa = RSA.Create(2048);
        var der = rsa.ExportPkcs8PrivateKey();
    var pem = PemEncoding.Write("PRIVATE KEY", der);
    return new string(pem);
    }

    static string EcPkcs8Pem()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var der = ec.ExportPkcs8PrivateKey();
    var pem = PemEncoding.Write("PRIVATE KEY", der);
    return new string(pem);
    }

    [TestMethod]
    public async Task ImportRsaPem_Signing_SetsSigUseAndStores()
    {
        using var db = NewDb(nameof(ImportRsaPem_Signing_SetsSigUseAndStores));
        var providerId = SeedProvider(db);
        var page = new IndexModel(db)
        {
            Input = new IndexModel.InputModel
            {
                Purpose = "Signing",
                Alg = "RS256",
                Kid = "kid-rsa",
                Active = true,
                JwkJson = RsaPkcs8Pem()
            }
        };

    var result = await page.OnPostAddAsync(providerId);
    Assert.IsNotNull(result);

        var saved = await db.IdentityProviderKeys.Where(k => k.IdentityProviderId == providerId).SingleAsync();
        Assert.AreEqual("RS256", saved.Alg);
        Assert.AreEqual("kid-rsa", saved.Kid);
        StringAssert.Contains(saved.Jwk, "\"kty\":\"RSA\"");
        StringAssert.Contains(saved.Jwk, "\"use\":\"sig\"");
    }

    [TestMethod]
    public async Task ImportEcPem_Encryption_SetsEncUseAndStores()
    {
        using var db = NewDb(nameof(ImportEcPem_Encryption_SetsEncUseAndStores));
        var providerId = SeedProvider(db);
        var page = new IndexModel(db)
        {
            Input = new IndexModel.InputModel
            {
                Purpose = "Encryption",
                Alg = "ECDH-ES",
                Kid = "kid-ec",
                Active = true,
                JwkJson = EcPkcs8Pem()
            }
        };

    var result = await page.OnPostAddAsync(providerId);
    Assert.IsNotNull(result);

        var saved = await db.IdentityProviderKeys.Where(k => k.IdentityProviderId == providerId).SingleAsync();
        Assert.AreEqual("ECDH-ES", saved.Alg);
        Assert.AreEqual("kid-ec", saved.Kid);
        StringAssert.Contains(saved.Jwk, "\"kty\":\"EC\"");
        StringAssert.Contains(saved.Jwk, "\"use\":\"enc\"");
        StringAssert.Contains(saved.Jwk, "\"crv\":\"P-256\"");
    }

    [TestMethod]
    public async Task InvalidPem_ReturnsModelError_NoInsert()
    {
        using var db = NewDb(nameof(InvalidPem_ReturnsModelError_NoInsert));
        var providerId = SeedProvider(db);
        var page = new IndexModel(db)
        {
            Input = new IndexModel.InputModel
            {
                Purpose = "Signing",
                Alg = "RS256",
                Kid = "any",
                Active = true,
                JwkJson = "-----BEGIN PRIVATE KEY-----\ninvalid\n-----END PRIVATE KEY-----\n"
            }
        };

    var result = await page.OnPostAddAsync(providerId);
    Assert.IsNotNull(result);
        Assert.IsTrue(page.ModelState.ContainsKey("Input.JwkJson"), "Expected model error for invalid PEM");
        Assert.AreEqual(0, db.IdentityProviderKeys.Count());
    }

    [TestMethod]
    public async Task AlgKtyMismatch_EcPemWithRsAlg_Errors()
    {
        using var db = NewDb(nameof(AlgKtyMismatch_EcPemWithRsAlg_Errors));
        var providerId = SeedProvider(db);
        var page = new IndexModel(db)
        {
            Input = new IndexModel.InputModel
            {
                Purpose = "Signing",
                Alg = "RS256", // mismatch for EC key
                Kid = "kid-ec",
                Active = true,
                JwkJson = EcPkcs8Pem()
            }
        };

    var result = await page.OnPostAddAsync(providerId);
    Assert.IsNotNull(result);
        Assert.IsTrue(page.ModelState.ContainsKey("Input.Alg"), "Expected model error for alg/kty mismatch");
        Assert.AreEqual(0, db.IdentityProviderKeys.Count());
    }

    [TestMethod]
    public async Task DuplicateKid_IsRejected()
    {
        using var db = NewDb(nameof(DuplicateKid_IsRejected));
        var providerId = SeedProvider(db);
        db.IdentityProviderKeys.Add(new IdentityProviderKey
        {
            Id = Guid.NewGuid(),
            IdentityProviderId = providerId,
            Purpose = IdentityProviderKeyPurpose.Signing,
            Alg = "RS256",
            Kid = "dup",
            Active = true,
            Jwk = "{\"kty\":\"RSA\",\"n\":\"x\",\"e\":\"AQAB\"}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();

        var page = new IndexModel(db)
        {
            Input = new IndexModel.InputModel
            {
                Purpose = "Signing",
                Alg = "RS256",
                Kid = "dup",
                Active = true,
                JwkJson = RsaPkcs8Pem()
            }
        };

    var result = await page.OnPostAddAsync(providerId);
    Assert.IsNotNull(result);
        Assert.IsTrue(page.ModelState.ContainsKey("Input.Kid"), "Expected model error for duplicate kid");
        Assert.AreEqual(1, db.IdentityProviderKeys.Count());
    }

    [TestMethod]
    public async Task ExpiresAt_PersistsToDatabase()
    {
        using var db = NewDb(nameof(ExpiresAt_PersistsToDatabase));
        var providerId = SeedProvider(db);
        var expires = DateTimeOffset.UtcNow.AddDays(30).ToOffset(TimeSpan.Zero); // normalize for deterministic compare
        var page = new IndexModel(db)
        {
            Input = new IndexModel.InputModel
            {
                Purpose = "Signing",
                Alg = "RS256",
                Kid = "kid-exp",
                Active = false,
                JwkJson = RsaPkcs8Pem(),
                ExpiresAt = expires
            }
        };

        var result = await page.OnPostAddAsync(providerId);
        Assert.IsNotNull(result);

        var saved = await db.IdentityProviderKeys.Where(k => k.IdentityProviderId == providerId && k.Kid == "kid-exp").SingleAsync();
        Assert.AreEqual(expires.ToUnixTimeSeconds(), saved.ExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [TestMethod]
    public async Task EcCurveMismatch_WithAlg_ES384_OnP256_Errors()
    {
        using var db = NewDb(nameof(EcCurveMismatch_WithAlg_ES384_OnP256_Errors));
        var providerId = SeedProvider(db);
        var page = new IndexModel(db)
        {
            Input = new IndexModel.InputModel
            {
                Purpose = "Signing",
                Alg = "ES384", // expects P-384, but we'll provide P-256 key
                Kid = "kid-ec384",
                Active = true,
                JwkJson = EcPkcs8Pem()
            }
        };

        var result = await page.OnPostAddAsync(providerId);
        Assert.IsNotNull(result);
        Assert.IsTrue(page.ModelState.ContainsKey("Input.Alg"), "Expected model error for ES384 on P-256 curve");
        Assert.AreEqual(0, db.IdentityProviderKeys.Count());
    }
}
