extern alias rpweb;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.TestSupport;
using BackchannelOptions = rpweb::MrWhoOidc.Web.Backchannel.BackchannelOptions;
using IBackchannelConfigurationProvider = rpweb::MrWhoOidc.Web.Backchannel.IBackchannelConfigurationProvider;
using LogoutTokenValidator = rpweb::MrWhoOidc.Web.Backchannel.LogoutTokenValidator;
using MemoryReplayCache = rpweb::MrWhoOidc.Web.Backchannel.MemoryReplayCache;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class LogoutTokenValidatorTests
{
    [TestMethod]
    public async Task ValidateAsync_AcceptsValidSidLogoutToken()
    {
        var validator = CreateValidator();
        var token = CreateLogoutToken(sid: "sid-123");

        var result = await validator.ValidateAsync(token);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("sid-123", result.Sid);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public async Task ValidateAsync_RejectsNonceClaim()
    {
        var validator = CreateValidator();
        var token = CreateLogoutToken(sid: "sid-123", extraClaims: [new Claim("nonce", "should-not-be-here")]);

        var result = await validator.ValidateAsync(token);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("nonce claim is not allowed", result.Error);
    }

    [TestMethod]
    public async Task ValidateAsync_RejectsOldLogoutToken()
    {
        var validator = CreateValidator();
        var token = CreateLogoutToken(
            sid: "sid-123",
            issuedAt: DateTimeOffset.UtcNow.AddMinutes(-20),
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(5));

        var result = await validator.ValidateAsync(token);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("logout token too old", result.Error);
    }

    private static LogoutTokenValidator CreateValidator()
    {
        var options = new BackchannelOptions
        {
            Enabled = true,
            Authority = "https://issuer.example.com",
            ClientId = "rp-client",
            AllowedClockSkew = TimeSpan.FromSeconds(30),
            MaxLogoutTokenAge = TimeSpan.FromMinutes(5),
            JtiTtl = TimeSpan.FromMinutes(10),
            JwksTtl = TimeSpan.FromMinutes(10)
        };

        return new LogoutTokenValidator(
            new StubBackchannelConfigurationProvider(options.Authority + "/", "https://issuer.example.com/jwks"),
            NullLogger<LogoutTokenValidator>.Instance,
            new StubHttpClientFactory(),
            new StubJwksCache(),
            new MemoryReplayCache(),
            options);
    }

    private static string CreateLogoutToken(
        string? sid = null,
        string? sub = null,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null,
        Claim[]? extraClaims = null)
    {
        var issued = issuedAt ?? DateTimeOffset.UtcNow;
        var expires = expiresAt ?? issued.AddMinutes(5);
        var claims = new List<Claim>
        {
            new("events", "{\"http://schemas.openid.net/event/backchannel-logout\":{}}"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        if (!string.IsNullOrEmpty(sid))
        {
            claims.Add(new Claim("sid", sid));
        }

        if (!string.IsNullOrEmpty(sub))
        {
            claims.Add(new Claim("sub", sub));
        }

        if (extraClaims is not null)
        {
            claims.AddRange(extraClaims);
        }

        var token = new JwtSecurityTokenHandler().CreateJwtSecurityToken(
            issuer: "https://issuer.example.com/",
            audience: "rp-client",
            subject: new ClaimsIdentity(claims),
            notBefore: issued.UtcDateTime,
            expires: expires.UtcDateTime,
            issuedAt: issued.UtcDateTime,
            signingCredentials: SharedTestKeys.GetRsaSigningCredentials());

        token.Header["typ"] = "logout+jwt";
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class StubBackchannelConfigurationProvider(string issuer, string jwksUri) : IBackchannelConfigurationProvider
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(string authority, CancellationToken ct = default)
        {
            return Task.FromResult(new OpenIdConnectConfiguration
            {
                Issuer = issuer,
                JwksUri = jwksUri
            });
        }
    }

    private sealed class StubJwksCache : IJwksCache
    {
        private readonly JsonWebKeySet _set = CreateSet();

        public Task<JsonWebKeySet?> GetAsync(string jwksUri, TimeSpan ttl, IHttpClientFactory httpFactory, CancellationToken ct = default)
        {
            return Task.FromResult<JsonWebKeySet?>(_set);
        }

        private static JsonWebKeySet CreateSet()
        {
            var set = new JsonWebKeySet();
            set.Keys.Add(SharedTestKeys.GetRsaJsonWebKey());
            return set;
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler());
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}