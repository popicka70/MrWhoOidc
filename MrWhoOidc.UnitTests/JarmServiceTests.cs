using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.UnitTests.TestDoubles;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class JarmServiceTests
{
    [TestMethod]
    public async Task JarmService_DoesNotEncrypt_WhenClientHasJwksButNoAuthorizationEncSettings()
    {
        using var rsa = RSA.Create(2048);
        var jwksJson = BuildRsaJwksJson(rsa);

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "c1",
            PublicJwksJson = jwksJson,
            // No AuthorizationEncryptedResponseAlg/Enc => must remain signed-only
            AuthorizationEncryptedResponseAlg = null,
            AuthorizationEncryptedResponseEnc = null
        };

        var clients = new StubClientStore(client);
        var jwt = new RecordingJwtService();
        var keys = new StubCachedKeyProvider();

        var svc = new MrWhoOidc.Auth.Services.JarmService(clients, jwt, keys);

        var token = await svc.CreateSuccessResponseAsync("c1", "https://issuer", "code123", "query.jwt", state: null);

        Assert.AreEqual(3, token.Split('.').Length);
        Assert.AreEqual(1, jwt.SignedCount);
        Assert.AreEqual(0, jwt.EncryptedCount);
    }

    [TestMethod]
    public async Task JarmService_Encrypts_WhenClientOptsIn_WithSupportedAlgEnc_AndHasJwks()
    {
        using var rsa = RSA.Create(2048);
        var jwksJson = BuildRsaJwksJson(rsa);

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "c1",
            PublicJwksJson = jwksJson,
            AuthorizationEncryptedResponseAlg = SecurityAlgorithms.RsaOAEP,
            AuthorizationEncryptedResponseEnc = SecurityAlgorithms.Aes256CbcHmacSha512
        };

        var clients = new StubClientStore(client);
        var jwt = new RecordingJwtService();
        var keys = new StubCachedKeyProvider();

        var svc = new MrWhoOidc.Auth.Services.JarmService(clients, jwt, keys);

        var token = await svc.CreateSuccessResponseAsync("c1", "https://issuer", "code123", "query.jwt", state: "s1");

        Assert.AreEqual(5, token.Split('.').Length);
        Assert.AreEqual(0, jwt.SignedCount);
        Assert.AreEqual(1, jwt.EncryptedCount);
    }

    [TestMethod]
    public async Task JarmService_DoesNotEncrypt_WhenClientOptsIn_WithUnsupportedAlgEnc()
    {
        using var rsa = RSA.Create(2048);
        var jwksJson = BuildRsaJwksJson(rsa);

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "c1",
            PublicJwksJson = jwksJson,
            AuthorizationEncryptedResponseAlg = "RSA-OAEP-256",
            AuthorizationEncryptedResponseEnc = SecurityAlgorithms.Aes256CbcHmacSha512
        };

        var clients = new StubClientStore(client);
        var jwt = new RecordingJwtService();
        var keys = new StubCachedKeyProvider();

        var svc = new MrWhoOidc.Auth.Services.JarmService(clients, jwt, keys);

        var token = await svc.CreateSuccessResponseAsync("c1", "https://issuer", "code123", "query.jwt", state: null);

        Assert.AreEqual(3, token.Split('.').Length);
        Assert.AreEqual(1, jwt.SignedCount);
        Assert.AreEqual(0, jwt.EncryptedCount);
    }

    private static string BuildRsaJwksJson(RSA rsa, string kid = "enc1")
    {
        static string Base64Url(byte[] bytes)
        {
            var s = Convert.ToBase64String(bytes);
            return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        var p = rsa.ExportParameters(false);
        var n = Base64Url(p.Modulus!);
        var e = Base64Url(p.Exponent!);
        return $"{{\"keys\":[{{\"kty\":\"RSA\",\"use\":\"enc\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\"}}]}}";
    }

    private sealed class RecordingJwtService : IJwtService
    {
        public int SignedCount { get; private set; }
        public int EncryptedCount { get; private set; }

        public Task<string> CreateJwtAsync(string issuer, string audience, IEnumerable<System.Security.Claims.Claim> claims, DateTimeOffset expires, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
        {
            SignedCount++;
            return Task.FromResult("a.b.c");
        }

        public Task<string> CreateJwtEncryptedAsync(string issuer, string audience, IEnumerable<System.Security.Claims.Claim> claims, DateTimeOffset expires, EncryptingCredentials encryptingCredentials, string? nonce = null, string? accessTokenHash = null, DateTimeOffset? authTime = null, string? tokenType = null, CancellationToken ct = default)
        {
            EncryptedCount++;
            return Task.FromResult("a.b.c.d.e");
        }
    }

    private sealed class StubCachedKeyProvider : ICachedKeyProvider
    {
        public Task<SecurityKey> GetActiveSigningKeyAsync(CancellationToken ct = default)
        {
            return Task.FromResult<SecurityKey>(new JsonWebKey { Kid = "sig1", Kty = "RSA", Alg = "RS256", N = "n", E = "e", D = "d" });
        }

        public Task<IReadOnlyCollection<JsonWebKey>> GetPublicJwksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<JsonWebKey>>(Array.Empty<JsonWebKey>());

        public void InvalidateCache() { }
    }
}
