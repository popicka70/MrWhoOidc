using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Client.Discovery;
using MrWhoOidc.Client.Jwks;
using MrWhoOidc.Client.Logout;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class MrWhoLogoutManagerTests
{
    [TestMethod]
    public async Task BuildFrontChannelLogoutAsync_IncludesSidAndState()
    {
        var options = new StaticOptionsMonitor(new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            Logout = { EnableFrontChannel = true }
        });
        var discovery = new StubDiscoveryClient(new MrWhoDiscoveryDocument
        {
            EndSessionEndpoint = "https://issuer.example.com/connect/endsession"
        });

        var manager = new MrWhoLogoutManager(discovery, new StubJwksCache(), options, new MemoryCache(new MemoryCacheOptions()), NullLogger<MrWhoLogoutManager>.Instance);

        var request = await manager.BuildFrontChannelLogoutAsync(new FrontChannelLogoutOptions
        {
            PostLogoutRedirectUri = new Uri("https://app.local/signed-out"),
            Sid = "session-123"
        });

        Assert.IsNotNull(request.LogoutUri);
        Assert.IsFalse(string.IsNullOrEmpty(request.State));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.LogoutUri.Query);
        Assert.AreEqual("session-123", query["sid"].ToString());
        Assert.AreEqual("client", query["client_id"].ToString());
        Assert.AreEqual("https://app.local/signed-out", query["post_logout_redirect_uri"].ToString());
    }

    [TestMethod]
    public async Task ValidateBackchannelLogoutAsync_ReturnsSid()
    {
        var keyBytes = new byte[32];
        RandomNumberGenerator.Fill(keyBytes);
        var signingKey = new SymmetricSecurityKey(keyBytes) { KeyId = "sig" };
        var jwks = new JsonWebKeySet();
        jwks.Keys.Add(JsonWebKeyConverter.ConvertFromSecurityKey(signingKey));

        var options = new StaticOptionsMonitor(new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com/",
            ClientId = "client",
            Logout = { EnableBackchannel = true, BackchannelReplayCacheDuration = TimeSpan.FromMinutes(5) }
        });
        var discovery = new StubDiscoveryClient(new MrWhoDiscoveryDocument
        {
            EndSessionEndpoint = "https://issuer.example.com/connect/endsession"
        });
        var jwksCache = new StubJwksCache(jwks);
        var manager = new MrWhoLogoutManager(discovery, jwksCache, options, new MemoryCache(new MemoryCacheOptions()), NullLogger<MrWhoLogoutManager>.Instance);

        var logoutToken = CreateLogoutToken(signingKey, options.CurrentValue.Issuer!, options.CurrentValue.ClientId!, sid: "sid-1");
        var result = await manager.ValidateBackchannelLogoutAsync(logoutToken);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("sid-1", result.Sid);
        Assert.IsNotNull(result.JwtId);
    }

    [TestMethod]
    public async Task ValidateBackchannelLogoutAsync_DetectsReplay()
    {
        var keyBytes = new byte[32];
        RandomNumberGenerator.Fill(keyBytes);
        var signingKey = new SymmetricSecurityKey(keyBytes) { KeyId = "sig" };
        var jwks = new JsonWebKeySet();
        jwks.Keys.Add(JsonWebKeyConverter.ConvertFromSecurityKey(signingKey));

        var options = new StaticOptionsMonitor(new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com/",
            ClientId = "client",
            Logout = { EnableBackchannel = true, BackchannelReplayCacheDuration = TimeSpan.FromMinutes(5) }
        });
        var discovery = new StubDiscoveryClient(new MrWhoDiscoveryDocument
        {
            EndSessionEndpoint = "https://issuer.example.com/connect/endsession"
        });
        var jwksCache = new StubJwksCache(jwks);
        var manager = new MrWhoLogoutManager(discovery, jwksCache, options, new MemoryCache(new MemoryCacheOptions()), NullLogger<MrWhoLogoutManager>.Instance);

        var logoutToken = CreateLogoutToken(signingKey, options.CurrentValue.Issuer!, options.CurrentValue.ClientId!, sid: "sid-1");
        var first = await manager.ValidateBackchannelLogoutAsync(logoutToken);
        Assert.IsTrue(first.Success);

        var second = await manager.ValidateBackchannelLogoutAsync(logoutToken);
        Assert.IsFalse(second.Success);
        Assert.AreEqual("replay_detected", second.Error);
    }

    private static string CreateLogoutToken(SecurityKey signingKey, string issuer, string audience, string? sid = null, string? sub = null)
    {
        var handler = new JwtSecurityTokenHandler();
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

        var token = handler.CreateJwtSecurityToken(
            issuer: issuer,
            audience: audience,
            subject: new ClaimsIdentity(claims),
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

        token.Header["typ"] = "logout+jwt";

        return handler.WriteToken(token);
    }

    private sealed class StubDiscoveryClient : IMrWhoDiscoveryClient
    {
        private readonly MrWhoDiscoveryDocument _document;

        public StubDiscoveryClient(MrWhoDiscoveryDocument document)
        {
            _document = document;
        }

        public ValueTask<MrWhoDiscoveryDocument> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_document);
    }

    private sealed class StubJwksCache : IMrWhoJwksCache
    {
        private readonly JsonWebKeySet _jwks;

        public StubJwksCache(JsonWebKeySet? jwks = null)
        {
            _jwks = jwks ?? new JsonWebKeySet("{\"keys\":[]}");
        }

        public ValueTask<JsonWebKeySet> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_jwks);

        public void Invalidate()
        {
        }
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<MrWhoOidcClientOptions>
    {
        public StaticOptionsMonitor(MrWhoOidcClientOptions value)
        {
            CurrentValue = value;
        }

        public MrWhoOidcClientOptions CurrentValue { get; set; }
        public MrWhoOidcClientOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<MrWhoOidcClientOptions, string?> listener) => null;
    }
}
