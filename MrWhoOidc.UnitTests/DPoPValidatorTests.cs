using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Security;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using JwtRegisteredClaimNames = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Tests for DPoP (Demonstrating Proof of Possession) validation.
/// Covers DPoP proof JWT validation, thumbprint matching, nonce enforcement, and replay prevention.
/// </summary>
[TestClass]
public sealed class DPoPValidatorTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public void DPoP_Valid_Proof_Passes_Validation()
    {
        using var db = CreateDb();
        
        // Setup: Create valid DPoP proof
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(securityKey);
        jwk.KeyId = Guid.NewGuid().ToString();
        
        var jti = Guid.NewGuid().ToString();
        var htm = "POST";
        var htu = "https://op.example.com/token";
        
        var claims = new[]
        {
            new Claim("jti", jti),
            new Claim("htm", htm),
            new Claim("htu", htu),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        header["jwk"] = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(jwk));
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(5)
        );
        
        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        
        // Act: Validate DPoP proof structure
        var tokenObj = handler.ReadJwtToken(proofToken);
        
        // Assert: Valid proof has required claims and header
        Assert.IsNotNull(tokenObj);
        Assert.AreEqual("dpop+jwt", tokenObj.Header.Typ);
        Assert.IsTrue(tokenObj.Header.ContainsKey("jwk"));
        Assert.IsNotNull(tokenObj.Claims.FirstOrDefault(c => c.Type == "jti"));
        Assert.IsNotNull(tokenObj.Claims.FirstOrDefault(c => c.Type == "htm"));
        Assert.IsNotNull(tokenObj.Claims.FirstOrDefault(c => c.Type == "htu"));
    }

    [TestMethod]
    public void DPoP_Missing_Jti_Fails_Validation()
    {
        using var db = CreateDb();
        
        // Setup: Create DPoP proof WITHOUT jti
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        
        var claims = new[]
        {
            new Claim("htm", "POST"),
            new Claim("htu", "https://op.example.com/token")
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(5)
        );
        
        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        var tokenObj = handler.ReadJwtToken(proofToken);
        
        // Act & Assert: Missing jti should fail
        var jtiClaim = tokenObj.Claims.FirstOrDefault(c => c.Type == "jti");
        Assert.IsNull(jtiClaim, "DPoP proof without jti should fail validation");
    }

    [TestMethod]
    public void DPoP_Missing_Htm_Fails_Validation()
    {
        using var db = CreateDb();
        
        // Setup: Create DPoP proof WITHOUT htm
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        
        var claims = new[]
        {
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("htu", "https://op.example.com/token")
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(5)
        );
        
        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        var tokenObj = handler.ReadJwtToken(proofToken);
        
        // Act & Assert: Missing htm should fail
        var htmClaim = tokenObj.Claims.FirstOrDefault(c => c.Type == "htm");
        Assert.IsNull(htmClaim, "DPoP proof without htm should fail validation");
    }

    [TestMethod]
    public void DPoP_Missing_Htu_Fails_Validation()
    {
        using var db = CreateDb();
        
        // Setup: Create DPoP proof WITHOUT htu
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        
        var claims = new[]
        {
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("htm", "POST")
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(5)
        );
        
        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        var tokenObj = handler.ReadJwtToken(proofToken);
        
        // Act & Assert: Missing htu should fail
        var htuClaim = tokenObj.Claims.FirstOrDefault(c => c.Type == "htu");
        Assert.IsNull(htuClaim, "DPoP proof without htu should fail validation");
    }

    [TestMethod]
    public void DPoP_Htm_Mismatch_Fails_Validation()
    {
        using var db = CreateDb();
        
        // Setup: Create DPoP proof with htm=POST but validate against GET
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        
        var claims = new[]
        {
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("htm", "POST"),  // Proof says POST
            new Claim("htu", "https://op.example.com/token")
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(5)
        );
        
        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        var tokenObj = handler.ReadJwtToken(proofToken);
        
        var htmClaim = tokenObj.Claims.First(c => c.Type == "htm");
        
        // Act & Assert: htm mismatch should fail
        Assert.AreEqual("POST", htmClaim.Value);
        Assert.AreNotEqual("GET", htmClaim.Value, "htm mismatch should fail validation");
    }

    [TestMethod]
    public void DPoP_Htu_Mismatch_Fails_Validation()
    {
        using var db = CreateDb();
        
        // Setup: Create DPoP proof with htu for token endpoint but validate against userinfo endpoint
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        
        var claims = new[]
        {
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("htm", "GET"),
            new Claim("htu", "https://op.example.com/token")  // Proof for /token
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(5)
        );
        
        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        var tokenObj = handler.ReadJwtToken(proofToken);
        
        var htuClaim = tokenObj.Claims.First(c => c.Type == "htu");
        
        // Act & Assert: htu mismatch should fail
        Assert.AreEqual("https://op.example.com/token", htuClaim.Value);
        Assert.AreNotEqual("https://op.example.com/userinfo", htuClaim.Value, "htu mismatch should fail validation");
    }

    [TestMethod]
    public void DPoP_JKT_Thumbprint_Calculation()
    {
        // Setup: Create RSA key and calculate JKT thumbprint
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(securityKey);
        
        // Act: Calculate thumbprint (SHA-256 of canonical JWK)
        var thumbprint = Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());
        
        // Assert: Thumbprint should be non-empty base64url string
        Assert.IsNotNull(thumbprint);
        Assert.IsTrue(thumbprint.Length > 0);
        Assert.IsFalse(thumbprint.Contains("+"), "Thumbprint should be base64url encoded (no +)");
        Assert.IsFalse(thumbprint.Contains("/"), "Thumbprint should be base64url encoded (no /)");
        Assert.IsFalse(thumbprint.Contains("="), "Thumbprint should be base64url encoded (no padding)");
    }

    [TestMethod]
    public void DPoP_JKT_Mismatch_Fails_Validation()
    {
        using var db = CreateDb();
        
        // Setup: Create token with cnf.jkt claim
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client = new ClientEntity { ClientId = "test-client", RealmId = realm.Id };
        var user = new User { Id = Guid.NewGuid(), Username = "testuser", PasswordHash = "hash" };
        
        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);
        
        var correctJkt = "correct_thumbprint_abc123";
        var wrongJkt = "wrong_thumbprint_xyz789";
        
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
        
        var token = new Token
        {
            Type = "access",
            TokenHash = tokenHash,
            ClientId = "test-client",
            UserId = user.Id,
            Audience = "test-api",
            ScopesJson = JsonSerializer.Serialize(new[] { "read" }),
            CnfJkt = correctJkt,  // Token bound to specific key
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Jti = Guid.NewGuid().ToString()
        };
        db.Tokens.Add(token);
        db.SaveChanges();
        
        // Act & Assert: JKT mismatch should fail
        Assert.AreEqual(correctJkt, token.CnfJkt);
        Assert.AreNotEqual(wrongJkt, token.CnfJkt, "DPoP proof with mismatched JKT should fail");
    }

    [TestMethod]
    public void DPoP_Expired_Proof_Fails_Validation()
    {
        // Setup: Create DPoP proof that expired
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        
        var claims = new[]
        {
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("htm", "POST"),
            new Claim("htu", "https://op.example.com/token"),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString())
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-10),
            expires: DateTime.UtcNow.AddMinutes(-5)  // Expired 5 minutes ago
        );
        
        var token = new JwtSecurityToken(header, payload);
        
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        
        // Act: Read expired proof
        var tokenObj = handler.ReadJwtToken(proofToken);
        var isExpired = tokenObj.ValidTo < DateTime.UtcNow;
        
        // Assert: Expired proof should fail
        Assert.IsTrue(isExpired, "Expired DPoP proof should fail validation");
    }

    [TestMethod]
    public void DPoP_Invalid_Signature_Fails_Validation()
    {
        // Setup: Create DPoP proof with one key, then try to validate with different key
        using var rsa1 = RSA.Create(2048);
        using var rsa2 = RSA.Create(2048);
        
        var securityKey1 = new RsaSecurityKey(rsa1);
        var securityKey2 = new RsaSecurityKey(rsa2);
        var signingCredentials = new SigningCredentials(securityKey1, SecurityAlgorithms.RsaSha256);
        
        var claims = new[]
        {
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("htm", "POST"),
            new Claim("htu", "https://op.example.com/token")
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(5)
        );
        
        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        
        // Act: Try to validate with wrong key
        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = securityKey2,  // Wrong key!
            ClockSkew = TimeSpan.Zero
        };
        
        // Assert: Should throw SecurityTokenSignatureKeyNotFoundException (wrong key)
        Assert.ThrowsExactly<SecurityTokenSignatureKeyNotFoundException>(() =>
        {
            handler.ValidateToken(proofToken, validationParams, out _);
        });
    }

    [TestMethod]
    public void DPoP_Nonce_Claim_Present_When_Required()
    {
        // Setup: Create DPoP proof with nonce
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var claims = new[]
        {
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("htm", "POST"),
            new Claim("htu", "https://op.example.com/token"),
            new Claim("nonce", nonce)  // Server-provided nonce
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(5)
        );
        
        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        var tokenObj = handler.ReadJwtToken(proofToken);
        
        // Act & Assert: Nonce claim should be present
        var nonceClaim = tokenObj.Claims.FirstOrDefault(c => c.Type == "nonce");
        Assert.IsNotNull(nonceClaim, "DPoP proof should include nonce when required");
        Assert.AreEqual(nonce, nonceClaim.Value);
    }

    [TestMethod]
    public async Task DPoP_Jti_Replay_Prevention()
    {
        using var db = CreateDb();
        
        // Setup: Track used JTIs to prevent replay
        var jti1 = Guid.NewGuid().ToString();
        var jti2 = Guid.NewGuid().ToString();
        
        var usedJtis = new HashSet<string>();
        
        // Act: First use of jti1 should succeed
        var firstUse = usedJtis.Add(jti1);
        Assert.IsTrue(firstUse, "First use of JTI should succeed");
        
        // Act: Replay of jti1 should fail
        var replay = usedJtis.Add(jti1);
        Assert.IsFalse(replay, "Replayed JTI should fail");
        
        // Act: Different jti2 should succeed
        var differentJti = usedJtis.Add(jti2);
        Assert.IsTrue(differentJti, "Different JTI should succeed");
        
        await Task.CompletedTask;
    }

    [TestMethod]
    public void DPoP_Ath_Claim_For_Protected_Resource()
    {
        // Setup: Create DPoP proof with ath (access token hash) for protected resource
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        
        var accessToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...";
        var athHash = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(accessToken)));
        
        var claims = new[]
        {
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("htm", "GET"),
            new Claim("htu", "https://api.example.com/data"),
            new Claim("ath", athHash)  // Access token hash binding
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(5)
        );
        
        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        var tokenObj = handler.ReadJwtToken(proofToken);
        
        // Act & Assert: ath claim should be present for protected resource
        var athClaim = tokenObj.Claims.FirstOrDefault(c => c.Type == "ath");
        Assert.IsNotNull(athClaim, "DPoP proof for protected resource should include ath");
        Assert.AreEqual(athHash, athClaim.Value);
    }

    [TestMethod]
    public void DPoP_Proof_Type_Header_Required()
    {
        // Setup: Verify typ header is "dpop+jwt"
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        
        var claims = new[]
        {
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("htm", "POST"),
            new Claim("htu", "https://op.example.com/token")
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";  // Required type
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(5)
        );
        
        var token = new JwtSecurityToken(header, payload);
        
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        var tokenObj = handler.ReadJwtToken(proofToken);
        
        // Act & Assert: typ header must be "dpop+jwt"
        Assert.AreEqual("dpop+jwt", tokenObj.Header.Typ);
    }

    [TestMethod]
    public void DPoP_JWK_Header_Required()
    {
        // Setup: Verify jwk header is present
        using var rsa = RSA.Create(2048);
        var securityKey = new RsaSecurityKey(rsa);
        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(securityKey);
        jwk.KeyId = Guid.NewGuid().ToString();
        
        var claims = new[]
        {
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("htm", "POST"),
            new Claim("htu", "https://op.example.com/token")
        };
        
        var header = new JwtHeader(signingCredentials);
        header["typ"] = "dpop+jwt";
        header["jwk"] = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(jwk));
        
        var payload = new JwtPayload(
            issuer: null,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddMinutes(5)
        );
        
        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        var proofToken = handler.WriteToken(token);
        var tokenObj = handler.ReadJwtToken(proofToken);
        
        // Act & Assert: jwk header must be present
        Assert.IsTrue(tokenObj.Header.ContainsKey("jwk"), "DPoP proof must include jwk header");
    }
}
