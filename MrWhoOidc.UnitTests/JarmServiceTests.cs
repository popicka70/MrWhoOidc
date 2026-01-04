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
using MrWhoOidc.UnitTests.TestSupport;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class JarmServiceTests
{
    // Build JWKS JSON from the shared RSA key (only done once)
    private static readonly string SharedRsaJwksJson = BuildRsaJwksJsonFromSharedKey();

    private static string BuildRsaJwksJsonFromSharedKey(string kid = "enc1")
    {
        static string Base64Url(byte[] bytes)
        {
            var s = Convert.ToBase64String(bytes);
            return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        var p = SharedTestKeys.Rsa2048.ExportParameters(false);
        var n = Base64Url(p.Modulus!);
        var e = Base64Url(p.Exponent!);
        return $"{{\"keys\":[{{\"kty\":\"RSA\",\"use\":\"enc\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\"}}]}}";
    }

    [TestMethod]
    public async Task JarmService_DoesNotEncrypt_WhenClientHasJwksButNoAuthorizationEncSettings()
    {
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "c1",
            PublicJwksJson = SharedRsaJwksJson,
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
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "c1",
            PublicJwksJson = SharedRsaJwksJson,
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
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "c1",
            PublicJwksJson = SharedRsaJwksJson,
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
