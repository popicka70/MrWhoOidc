using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.UnitTests.Helpers;
using System.Text.Json;
using MrWhoOidc.Auth.Crypto;
using System.Security.Cryptography;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class RequestObjectValidatorTests
{
    private sealed class NoopRequestObjectDecryptor : IRequestObjectDecryptor
    {
        public Task<string?> TryDecryptToInnerJwtAsync(string requestObject, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static IOptions<AuthOptions> Options() => Microsoft.Extensions.Options.Options.Create(new AuthOptions());

    [TestMethod]
    public async Task ValidateAsync_Fails_WhenNoKeyConfigured()
    {
        using var db = CreateDb();
        db.Clients.Add(new ClientEntity { ClientId = "c1" });
        await db.SaveChangesAsync();

        var validator = new RequestObjectValidator(db, new ConfigurationBuilder().Build(), NullLogger<RequestObjectValidator>.Instance, Options(), new InMemoryJarReplayCache(), new NoopRequestObjectDecryptor());
        var (jwt, kid) = CreateSignedRequest("c1", "https://as/authorize", out var jwk);
        var result = await validator.ValidateAsync(jwt, "https://as/authorize");
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request_object", result.Error);
    }

    [TestMethod]
    public async Task ValidateAsync_Detects_Replay_By_Jti()
    {
        using var db = CreateDb();
        // Build a signed request with explicit jti so replay cache is exercised
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var parameters = rsa.ExportParameters(true);
        var n = Base64UrlEncoder.Encode(parameters.Modulus);
        var e = Base64UrlEncoder.Encode(parameters.Exponent);
        var d = Base64UrlEncoder.Encode(parameters.D);
        var p = Base64UrlEncoder.Encode(parameters.P);
        var q = Base64UrlEncoder.Encode(parameters.Q);
        var dp = Base64UrlEncoder.Encode(parameters.DP);
        var dq = Base64UrlEncoder.Encode(parameters.DQ);
        var qi = Base64UrlEncoder.Encode(parameters.InverseQ);
        var kid = Guid.NewGuid().ToString("N");
        var jwkJson = $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\",\"d\":\"{d}\",\"p\":\"{p}\",\"q\":\"{q}\",\"dp\":\"{dp}\",\"dq\":\"{dq}\",\"qi\":\"{qi}\"}}";
        var jwk = new JsonWebKey(jwkJson);
        var creds = new SigningCredentials(jwk, SecurityAlgorithms.RsaSha256);
        var clientId = "c1";
        var aud = "https://as/authorize";
        var jti = Guid.NewGuid().ToString("N");
        var token = new JwtSecurityToken(
            issuer: clientId,
            audience: aud,
            claims: new[] { new Claim("client_id", clientId), new Claim("response_type", "code"), new Claim("redirect_uri", "https://cb"), new Claim(JwtRegisteredClaimNames.Jti, jti) },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds
        );
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        db.Clients.Add(new ClientEntity { ClientId = clientId, PublicJwksJson = jwkJson });
        await db.SaveChangesAsync();

        var cache = new InMemoryJarReplayCache();
        var validator = new RequestObjectValidator(db, new ConfigurationBuilder().Build(), NullLogger<RequestObjectValidator>.Instance, Options(), cache, new NoopRequestObjectDecryptor());
        // Sanity: JWT contains the expected jti
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
        Assert.AreEqual(jti, parsed.Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value);
        var ok = await validator.ValidateAsync(jwt, aud);
        Assert.IsTrue(ok.IsValid);
        // Second validation with same JWT should be rejected as replay
        var replay = await validator.ValidateAsync(jwt, aud);
        Assert.IsFalse(replay.IsValid);
        Assert.AreEqual("invalid_request_object", replay.Error);
    }

    [TestMethod]
    public async Task ValidateAsync_Allows_Expiring_At_Skew_Boundary()
    {
        using var db = CreateDb();
        var opts = Microsoft.Extensions.Options.Options.Create(new AuthOptions { RequestObjectClockSkewSeconds = 120 });

        // Build a JWT that expires now - but within skew window it should pass
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var parameters = rsa.ExportParameters(true);
        var n = Base64UrlEncoder.Encode(parameters.Modulus);
        var e = Base64UrlEncoder.Encode(parameters.Exponent);
        var d = Base64UrlEncoder.Encode(parameters.D);
        var p = Base64UrlEncoder.Encode(parameters.P);
        var q = Base64UrlEncoder.Encode(parameters.Q);
        var dp = Base64UrlEncoder.Encode(parameters.DP);
        var dq = Base64UrlEncoder.Encode(parameters.DQ);
        var qi = Base64UrlEncoder.Encode(parameters.InverseQ);
        var kid = Guid.NewGuid().ToString("N");
        var jwkJson = $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\",\"d\":\"{d}\",\"p\":\"{p}\",\"q\":\"{q}\",\"dp\":\"{dp}\",\"dq\":\"{dq}\",\"qi\":\"{qi}\"}}";
        var jwk = new JsonWebKey(jwkJson);
        var creds = new SigningCredentials(jwk, SecurityAlgorithms.RsaSha256);
        var aud = "https://as/authorize";
        var clientId = "c2";
        db.Clients.Add(new ClientEntity { ClientId = clientId, PublicJwksJson = jwkJson });
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: clientId,
            audience: aud,
            claims: new[] { new Claim("client_id", clientId), new Claim("response_type", "code"), new Claim("redirect_uri", "https://cb") },
            notBefore: now.AddMinutes(-1),
            expires: now.AddSeconds(-30), // already expired 30s ago
            signingCredentials: creds
        );
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        var validator = new RequestObjectValidator(db, new ConfigurationBuilder().Build(), NullLogger<RequestObjectValidator>.Instance, opts, new InMemoryJarReplayCache(), new NoopRequestObjectDecryptor());
        var result = await validator.ValidateAsync(jwt, aud);
        // Within 120s skew -> should still be accepted
        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidateAsync_Rejects_Expired_Beyond_Skew()
    {
        using var db = CreateDb();
        var opts = Microsoft.Extensions.Options.Options.Create(new AuthOptions { RequestObjectClockSkewSeconds = 60 });

        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var parameters = rsa.ExportParameters(true);
        var n = Base64UrlEncoder.Encode(parameters.Modulus);
        var e = Base64UrlEncoder.Encode(parameters.Exponent);
        var d = Base64UrlEncoder.Encode(parameters.D);
        var p = Base64UrlEncoder.Encode(parameters.P);
        var q = Base64UrlEncoder.Encode(parameters.Q);
        var dp = Base64UrlEncoder.Encode(parameters.DP);
        var dq = Base64UrlEncoder.Encode(parameters.DQ);
        var qi = Base64UrlEncoder.Encode(parameters.InverseQ);
        var kid = Guid.NewGuid().ToString("N");
        var jwkJson = $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\",\"d\":\"{d}\",\"p\":\"{p}\",\"q\":\"{q}\",\"dp\":\"{dp}\",\"dq\":\"{dq}\",\"qi\":\"{qi}\"}}";
        var jwk = new JsonWebKey(jwkJson);
        var creds = new SigningCredentials(jwk, SecurityAlgorithms.RsaSha256);
        var aud = "https://as/authorize";
        var clientId = "c3";
        db.Clients.Add(new ClientEntity { ClientId = clientId, PublicJwksJson = jwkJson });
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: clientId,
            audience: aud,
            claims: new[] { new Claim("client_id", clientId), new Claim("response_type", "code"), new Claim("redirect_uri", "https://cb") },
            notBefore: now.AddMinutes(-5),
            expires: now.AddSeconds(-121), // expired more than skew (60s)
            signingCredentials: creds
        );
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        var validator = new RequestObjectValidator(db, new ConfigurationBuilder().Build(), NullLogger<RequestObjectValidator>.Instance, opts, new InMemoryJarReplayCache(), new NoopRequestObjectDecryptor());
        var result = await validator.ValidateAsync(jwt, aud);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request_object", result.Error);
    }

    [TestMethod]
    public async Task ValidateAsync_Succeeds_WithClientJwk()
    {
        using var db = CreateDb();
        var (jwt, kid, jwkJson) = CreateSignedRequestWithJwk("c1", "https://as/authorize");
        db.Clients.Add(new ClientEntity { ClientId = "c1", PublicJwksJson = jwkJson });
        await db.SaveChangesAsync();

        var validator = new RequestObjectValidator(db, new ConfigurationBuilder().Build(), NullLogger<RequestObjectValidator>.Instance, Options(), new InMemoryJarReplayCache(), new NoopRequestObjectDecryptor());
        var result = await validator.ValidateAsync(jwt, "https://as/authorize");
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("c1", result.ClientId);
        Assert.IsNotNull(result.Request);
    }

    [TestMethod]
    public async Task ValidateAsync_Succeeds_WithEncryptedNestedRequestObject()
    {
        using var db = CreateDb();

        // Tenant context required for KeyStore-backed decryption
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var tenantId = tenantAccessor.CurrentTenant!.TenantId;

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = "t1",
            Name = "Tenant 1",
            IssuerUri = "https://as",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, tenantAccessor, new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var decryptor = new RequestObjectDecryptor(keyStore, NullLogger<RequestObjectDecryptor>.Instance);

        // Create and persist an OP encryption key (enc) and extract its public key for EncryptingCredentials.
        await keyStore.GetActiveEncryptionKeyAsync();
        var encRow = await db.SigningKeys
            .Where(k => k.Use == "enc")
            .OrderByDescending(k => k.CreatedAt)
            .FirstAsync();
        var encRsaJwk = JsonSerializer.Deserialize<RsaJwk>(encRow.JwkJson)!;
        using var encRsa = encRsaJwk.ToRSA();
        var encPublic = new RsaSecurityKey(encRsa) { KeyId = encRsaJwk.Kid };
        var encryptingCredentials = new EncryptingCredentials(encPublic, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512);

        // Client signing key (RSA) + public JWKS for validator.
        using var clientRsa = RSA.Create(2048);
        var clientKid = Guid.NewGuid().ToString("N");
        var clientJwk = RsaJwk.FromRSA(clientRsa, clientKid, alg: "RS256", includePrivate: true, use: "sig");
        var clientJwkJson = clientJwk.ToJson(includePrivate: true);
        var clientPublicJwkJson = clientJwk.ToJson(includePrivate: false);

        var clientId = "c1";
        db.Clients.Add(new ClientEntity { ClientId = clientId, PublicJwksJson = clientPublicJwkJson });
        await db.SaveChangesAsync();

        var signingKey = new JsonWebKey(clientJwkJson);
        var signingCreds = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);

        var aud = "https://as/authorize";
        var now = DateTime.UtcNow;
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = aud,
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = "https://cb",
                ["scope"] = "openid"
            },
            SigningCredentials = signingCreds,
            EncryptingCredentials = encryptingCredentials,
            TokenType = "JWT"
        };

        var encryptedRequestObject = handler.CreateEncodedJwt(descriptor);
        Assert.AreEqual(5, encryptedRequestObject.Split('.').Length, "Encrypted request object should be JWE compact");

        var validator = new RequestObjectValidator(db, new ConfigurationBuilder().Build(), NullLogger<RequestObjectValidator>.Instance, Options(), new InMemoryJarReplayCache(), decryptor);
        var result = await validator.ValidateAsync(encryptedRequestObject, aud);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(clientId, result.ClientId);
        Assert.IsNotNull(result.Request);
        Assert.AreEqual("code", result.Request!.response_type);
        Assert.AreEqual("https://cb", result.Request.redirect_uri);
    }

    private static (string jwt, string kid, string jwkJson) CreateSignedRequestWithJwk(string clientId, string aud)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var parameters = rsa.ExportParameters(true);
        var n = Base64UrlEncoder.Encode(parameters.Modulus);
        var e = Base64UrlEncoder.Encode(parameters.Exponent);
        var d = Base64UrlEncoder.Encode(parameters.D);
        var p = Base64UrlEncoder.Encode(parameters.P);
        var q = Base64UrlEncoder.Encode(parameters.Q);
        var dp = Base64UrlEncoder.Encode(parameters.DP);
        var dq = Base64UrlEncoder.Encode(parameters.DQ);
        var qi = Base64UrlEncoder.Encode(parameters.InverseQ);
        var kid = Guid.NewGuid().ToString("N");
        var jwk = $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\",\"d\":\"{d}\",\"p\":\"{p}\",\"q\":\"{q}\",\"dp\":\"{dp}\",\"dq\":\"{dq}\",\"qi\":\"{qi}\"}}";
        var creds = new SigningCredentials(new JsonWebKey(jwk), SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: clientId,
            audience: aud,
            claims: new[] { new Claim("client_id", clientId), new Claim("response_type", "code"), new Claim("redirect_uri", "https://cb") },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds
        );
        var handler = new JwtSecurityTokenHandler();
        return (handler.WriteToken(token), kid, jwk);
    }

    private static (string jwt, string kid) CreateSignedRequest(string clientId, string aud, out JsonWebKey jwk)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var parameters = rsa.ExportParameters(true);
        var n = Base64UrlEncoder.Encode(parameters.Modulus);
        var e = Base64UrlEncoder.Encode(parameters.Exponent);
        var d = Base64UrlEncoder.Encode(parameters.D);
        var p = Base64UrlEncoder.Encode(parameters.P);
        var q = Base64UrlEncoder.Encode(parameters.Q);
        var dp = Base64UrlEncoder.Encode(parameters.DP);
        var dq = Base64UrlEncoder.Encode(parameters.DQ);
        var qi = Base64UrlEncoder.Encode(parameters.InverseQ);
        var kid = Guid.NewGuid().ToString("N");
        var jwkJson = $"{{\"kty\":\"RSA\",\"alg\":\"RS256\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\",\"d\":\"{d}\",\"p\":\"{p}\",\"q\":\"{q}\",\"dp\":\"{dp}\",\"dq\":\"{dq}\",\"qi\":\"{qi}\"}}";
        jwk = new JsonWebKey(jwkJson);
        var creds = new SigningCredentials(jwk, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: clientId,
            audience: aud,
            claims: new[] { new Claim("client_id", clientId), new Claim("response_type", "code"), new Claim("redirect_uri", "https://cb") },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds
        );
        var handler = new JwtSecurityTokenHandler();
        return (handler.WriteToken(token), kid);
    }
}
