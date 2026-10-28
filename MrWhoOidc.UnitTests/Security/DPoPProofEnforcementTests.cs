using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Security;
using MrWhoOidc.WebAuth.Infrastructure;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// H2: <c>DPoPValidatorTests</c> only round-tripped claims and simulated the replay cache with a local
/// <c>HashSet</c>, so it could not establish that the production DPoP validator actually enforces
/// anything. These tests drive the real <see cref="DPoPValidator"/> (and the real replay/nonce path)
/// with signed proofs.
/// </summary>
[TestClass]
public sealed class DPoPProofEnforcementTests
{
    private const string Endpoint = "https://op.example.com/token";
    private const string AccessToken = "an-access-token-value";

    private static (SecurityKey Key, Dictionary<string, object> Jwk) CreateKey()
    {
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(false);
        var jwk = new Dictionary<string, object>
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64UrlEncoder.Encode(p.Q.X),
            ["y"] = Base64UrlEncoder.Encode(p.Q.Y)
        };

        return (new ECDsaSecurityKey(ec), jwk);
    }

    private static string CreateProof(
        SecurityKey key,
        Dictionary<string, object> jwk,
        string htm = "POST",
        string htu = Endpoint,
        long? iat = null,
        string? jti = null,
        string? ath = null,
        string typ = "dpop+jwt",
        Dictionary<string, object>? jwkOverride = null)
    {
        var credentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);
        var claims = new List<Claim>
        {
            new("jti", jti ?? Guid.NewGuid().ToString("N")),
            new("htm", htm),
            new("htu", htu),
            new(JwtRegisteredClaimNames.Iat,
                (iat ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(),
                ClaimValueTypes.Integer64)
        };

        var payload = new JwtPayload(null, null, claims, null, null);
        if (ath is not null)
        {
            payload["ath"] = ath;
        }

        var header = new JwtHeader(credentials) { ["typ"] = typ, ["jwk"] = jwkOverride ?? jwk };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private static DefaultHttpContext CreateHttp(string method = "POST", string? proof = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (proof is not null)
        {
            context.Request.Headers["DPoP"] = proof;
        }

        return context;
    }

    private static DPoPValidator CreateValidator(int? iatLeewaySeconds = null)
        => new(
            NullLogger<DPoPValidator>.Instance,
            iatLeewaySeconds is null
                ? null
                : Microsoft.Extensions.Options.Options.Create(new DPoPValidationOptions { IatLeewaySeconds = iatLeewaySeconds.Value }));

    [TestMethod]
    public async Task ValidProof_IsAccepted_AndYieldsTheJktJtiAndIat()
    {
        var (key, jwk) = CreateKey();
        var jti = Guid.NewGuid().ToString("N");
        var proof = CreateProof(key, jwk, jti: jti);

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);

        Assert.IsTrue(result.Ok, $"the proof must be accepted (error: {result.Error})");
        Assert.IsFalse(string.IsNullOrEmpty(result.Jkt), "the validator must return the key thumbprint");
        Assert.AreEqual(jti, result.Jti);
        Assert.IsNotNull(result.Iat);
    }

    [TestMethod]
    public async Task MissingProof_IsRejected()
    {
        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp(), Endpoint);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("missing_dpop", result.Error);
    }

    [TestMethod]
    public async Task WrongTokenType_IsRejected()
    {
        var (key, jwk) = CreateKey();
        var proof = CreateProof(key, jwk, typ: "jwt");

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("invalid_typ", result.Error);
    }

    [TestMethod]
    public async Task MissingJwkHeader_IsRejected()
    {
        var (key, _) = CreateKey();
        var proof = CreateProof(key, new Dictionary<string, object> { ["kty"] = "EC" });

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("unsupported_jwk", result.Error);
    }

    [TestMethod]
    public async Task JwkWithPrivateMembers_IsRejected()
    {
        var (key, jwk) = CreateKey();
        var leaky = new Dictionary<string, object>(jwk) { ["d"] = Base64UrlEncoder.Encode(new byte[32]) };

        var proof = CreateProof(key, jwk, jwkOverride: leaky);

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);

        Assert.IsFalse(result.Ok, "a proof that leaks the private key must be refused");
        Assert.AreEqual("unsupported_jwk", result.Error);
    }

    [TestMethod]
    public async Task ProofSignedWithAnotherKey_IsRejected()
    {
        var (_, jwkA) = CreateKey();
        var (keyB, _) = CreateKey();

        // The proof is signed with key B but advertises key A.
        var proof = CreateProof(keyB, jwkA);

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);

        Assert.IsFalse(result.Ok, "the signature must be verified against the embedded JWK");
        Assert.AreEqual("validation_error", result.Error);
    }

    [TestMethod]
    public async Task MissingRequiredClaims_AreRejected()
    {
        var (key, jwk) = CreateKey();
        var credentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);
        var payload = new JwtPayload(null, null, [new Claim("htm", "POST")], null, null);
        var header = new JwtHeader(credentials) { ["typ"] = "dpop+jwt", ["jwk"] = jwk };
        var proof = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("missing_claims", result.Error);
    }

    [TestMethod]
    public async Task HtmMismatch_IsRejected()
    {
        var (key, jwk) = CreateKey();
        var proof = CreateProof(key, jwk, htm: "GET");

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("htm_mismatch", result.Error);
    }

    [TestMethod]
    public async Task HtuMismatch_IsRejected()
    {
        var (key, jwk) = CreateKey();
        var proof = CreateProof(key, jwk, htu: "https://op.example.com/userinfo");

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("htu_mismatch", result.Error);
    }

    [TestMethod]
    public async Task IatInThePast_IsRejected()
    {
        var (key, jwk) = CreateKey();
        var old = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        var proof = CreateProof(key, jwk, iat: old);

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);

        Assert.IsFalse(result.Ok, "an old iat must be rejected");
        Assert.AreEqual("iat_out_of_range", result.Error);
    }

    [TestMethod]
    public async Task IatInTheFuture_IsRejected()
    {
        var (key, jwk) = CreateKey();
        var future = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var proof = CreateProof(key, jwk, iat: future);

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);

        Assert.IsFalse(result.Ok, "an iat in the future must be rejected");
        Assert.AreEqual("iat_out_of_range", result.Error);
    }

    [TestMethod]
    public async Task AthIsRequired_WhenAnAccessTokenIsPresented()
    {
        var (key, jwk) = CreateKey();
        var proof = CreateProof(key, jwk);

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint, AccessToken);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("missing_ath", result.Error);
    }

    [TestMethod]
    public async Task AthMismatch_IsRejected()
    {
        var (key, jwk) = CreateKey();
        var wrongHash = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes("another-token")));
        var proof = CreateProof(key, jwk, ath: wrongHash);

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint, AccessToken);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("ath_mismatch", result.Error);
    }

    [TestMethod]
    public async Task CorrectAth_IsAccepted()
    {
        var (key, jwk) = CreateKey();
        var hash = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(AccessToken)));
        var proof = CreateProof(key, jwk, ath: hash);

        var result = await CreateValidator().ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint, AccessToken);

        Assert.IsTrue(result.Ok, $"the proof must be accepted (error: {result.Error})");
    }

    [TestMethod]
    public async Task ConfiguredZeroIatLeeway_IsHonoured_NotSilentlyRaised()
    {
        var (key, jwk) = CreateKey();
        // 30 s old: inside the default 60 s window, outside a 0 s window.
        var proof = CreateProof(key, jwk, iat: DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds());

        var lenient = await CreateValidator(iatLeewaySeconds: 60)
            .ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);
        Assert.IsTrue(lenient.Ok, $"the default window must accept it (error: {lenient.Error})");

        var strict = await CreateValidator(iatLeewaySeconds: 0)
            .ValidateForEndpointAsync(CreateHttp("POST", proof), Endpoint);
        Assert.IsFalse(strict.Ok, "a configured leeway of 0 must not silently become the 60 s default");
        Assert.AreEqual("iat_out_of_range", strict.Error);
    }

    [TestMethod]
    public async Task ReplayedProof_IsRejectedByTheReplayCache()
    {
        var (key, jwk) = CreateKey();
        var jti = Guid.NewGuid().ToString("N");
        var proof = CreateProof(key, jwk, jti: jti);

        var validator = CreateValidator();
        var replayCache = new InMemoryDPoPReplayCache();

        var first = await DpopValidationHelper.ValidateForTokenEndpointAsync(
            validator, replayCache, CreateHttp("POST", proof), Endpoint, null);
        Assert.IsTrue(first.Ok, "the first use of the proof must be accepted");

        var second = await DpopValidationHelper.ValidateForTokenEndpointAsync(
            validator, replayCache, CreateHttp("POST", proof), Endpoint, null);
        Assert.IsFalse(second.Ok, "the replayed proof must be rejected");
        Assert.IsNull(second.Jkt);
    }

    [TestMethod]
    public async Task NonceStore_IssuesANonce_AndAcceptsOnlyTheIssuedOne()
    {
        var store = new InMemoryDPoPNonceStore();

        var (ok, nonce) = await store.ValidateOrIssueAsync(Endpoint, "203.0.113.10", "jkt", null);
        Assert.IsFalse(ok, "the first request must be told to use the nonce");
        Assert.IsFalse(string.IsNullOrEmpty(nonce));

        var (okWithNonce, _) = await store.ValidateOrIssueAsync(Endpoint, "203.0.113.10", "jkt", nonce);
        Assert.IsTrue(okWithNonce, "the issued nonce must be accepted");

        var (okWithWrongNonce, _) = await store.ValidateOrIssueAsync(Endpoint, "203.0.113.10", "jkt", "not-the-nonce");
        Assert.IsFalse(okWithWrongNonce, "a wrong nonce must be rejected");
    }

    [TestMethod]
    public async Task NonceIsBoundToTheClientAndTheKey()
    {
        var store = new InMemoryDPoPNonceStore();

        var (_, nonce) = await store.ValidateOrIssueAsync(Endpoint, "203.0.113.10", "jkt-a", null);

        // Another client must not be able to reuse the nonce.
        var (okOtherIp, _) = await store.ValidateOrIssueAsync(Endpoint, "203.0.113.11", "jkt-a", nonce);
        Assert.IsFalse(okOtherIp, "the nonce must be bound to the client");

        var (okOtherKey, _) = await store.ValidateOrIssueAsync(Endpoint, "203.0.113.10", "jkt-b", nonce);
        Assert.IsFalse(okOtherKey, "the nonce must be bound to the key");
    }
}
