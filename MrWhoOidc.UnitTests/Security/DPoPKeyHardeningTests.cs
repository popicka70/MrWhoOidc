using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Security;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Third 2026-10-04 review: the DPoP proof's jwk header was accepted with private members (d) and with any RSA size
/// or EC curve, although only ES256/RS256 proofs are allowed.
/// </summary>
[TestClass]
public sealed class DPoPKeyHardeningTests
{
    private const string Endpoint = "https://op.example.com/token";

    private static async Task<DPoPValidationResult> ValidateAsync(SecurityKey signingKey, string alg, Dictionary<string, object> jwk)
    {
        var header = new JwtHeader(new SigningCredentials(signingKey, alg)) { ["typ"] = "dpop+jwt", ["jwk"] = jwk };
        var payload = new JwtPayload(null, null, [
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("htm", "POST"),
            new Claim("htu", Endpoint),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)], null, null);
        var proof = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));

        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Headers["DPoP"] = proof;
        return await new DPoPValidator(NullLogger<DPoPValidator>.Instance).ValidateForEndpointAsync(http, Endpoint);
    }

    private static Dictionary<string, object> EcJwk(ECDsa ec, bool includePrivate)
    {
        var p = ec.ExportParameters(includePrivate);
        var jwk = new Dictionary<string, object>
        {
            ["kty"] = "EC",
            ["crv"] = p.Curve.Oid.FriendlyName switch { "nistP256" or "ECDSA_P256" => "P-256", "nistP384" or "ECDSA_P384" => "P-384", _ => "P-256" },
            ["x"] = Base64UrlEncoder.Encode(p.Q.X),
            ["y"] = Base64UrlEncoder.Encode(p.Q.Y),
        };
        if (includePrivate) jwk["d"] = Base64UrlEncoder.Encode(p.D);
        return jwk;
    }

    [TestMethod]
    public async Task PublicP256Key_IsAccepted()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var result = await ValidateAsync(new ECDsaSecurityKey(ec), SecurityAlgorithms.EcdsaSha256, EcJwk(ec, includePrivate: false));

        Assert.IsTrue(result.Ok, result.Error);
    }

    [TestMethod]
    public async Task JwkWithPrivateMembers_IsRejected()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var result = await ValidateAsync(new ECDsaSecurityKey(ec), SecurityAlgorithms.EcdsaSha256, EcJwk(ec, includePrivate: true));

        Assert.IsFalse(result.Ok);
    }

    [TestMethod]
    public async Task Rsa1024Key_IsRejected()
    {
        using var rsa = RSA.Create(1024);
        var p = rsa.ExportParameters(false);
        var jwk = new Dictionary<string, object> { ["kty"] = "RSA", ["n"] = Base64UrlEncoder.Encode(p.Modulus), ["e"] = Base64UrlEncoder.Encode(p.Exponent) };

        var result = await ValidateAsync(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256, jwk);

        Assert.IsFalse(result.Ok);
    }
}
