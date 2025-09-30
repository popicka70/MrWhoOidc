using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Client.Authorization;
using MrWhoOidc.Client.Discovery;
using MrWhoOidc.Client.Jwks;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class MrWhoAuthorizationManagerTests
{
    [TestMethod]
    public async Task BuildAuthorizeRequest_IncludesPkceAndNonce()
    {
        var options = new StaticOptionsMonitor(new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret",
            Scopes = new[] { "openid", "profile" }
        });
        var discovery = new StubDiscoveryClient(new MrWhoDiscoveryDocument
        {
            AuthorizationEndpoint = "https://issuer.example.com/authorize",
            TokenEndpoint = "https://issuer.example.com/token"
        });

    var manager = new MrWhoAuthorizationManager(discovery, options, new StubJwksCache(), new MemoryCache(new MemoryCacheOptions()), NullLogger<MrWhoAuthorizationManager>.Instance);

        var context = await manager.BuildAuthorizeRequestAsync(new Uri("https://app/callback"));

        Assert.IsNotNull(context.RequestUri);
    var query = QueryHelpers.ParseQuery(context.RequestUri.Query);
    Assert.AreEqual("S256", query["code_challenge_method"].ToString());
    Assert.IsFalse(string.IsNullOrEmpty(query["nonce"].ToString()));
    Assert.IsFalse(string.IsNullOrEmpty(query["code_challenge"].ToString()));
        Assert.IsFalse(string.IsNullOrEmpty(context.CodeVerifier));
    }

    [TestMethod]
    public async Task ValidateCallback_RejectsUnknownState()
    {
        var options = new StaticOptionsMonitor(new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret"
        });
        var discovery = new StubDiscoveryClient(new MrWhoDiscoveryDocument
        {
            AuthorizationEndpoint = "https://issuer.example.com/authorize"
        });
    var manager = new MrWhoAuthorizationManager(discovery, options, new StubJwksCache(), new MemoryCache(new MemoryCacheOptions()), NullLogger<MrWhoAuthorizationManager>.Instance);

        var result = await manager.ValidateCallbackAsync("missing", "code", null);
        Assert.IsTrue(result.IsError);
        Assert.AreEqual("invalid_state", result.Error);
    }

    [TestMethod]
    public async Task ValidateCallback_ReturnsCode()
    {
        var options = new StaticOptionsMonitor(new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret"
        });
        var discovery = new StubDiscoveryClient(new MrWhoDiscoveryDocument
        {
            AuthorizationEndpoint = "https://issuer.example.com/authorize"
        });
    var manager = new MrWhoAuthorizationManager(discovery, options, new StubJwksCache(), new MemoryCache(new MemoryCacheOptions()), NullLogger<MrWhoAuthorizationManager>.Instance);

        var context = await manager.BuildAuthorizeRequestAsync(new Uri("https://app/callback"));
        var result = await manager.ValidateCallbackAsync(context.State, "code", null);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("code", result.Code);
        Assert.AreEqual(context.CodeVerifier, result.CodeVerifier);
    }

    [TestMethod]
    public async Task BuildAuthorizeRequest_CreatesRequestObjectWhenJarEnabled()
    {
        var options = new StaticOptionsMonitor(new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret",
            Scopes = new[] { "openid", "profile" },
            Jar = { Enabled = true }
        });
        var discovery = new StubDiscoveryClient(new MrWhoDiscoveryDocument
        {
            Issuer = "https://issuer.example.com",
            AuthorizationEndpoint = "https://issuer.example.com/authorize",
            TokenEndpoint = "https://issuer.example.com/token"
        });

        var manager = new MrWhoAuthorizationManager(discovery, options, new StubJwksCache(), new MemoryCache(new MemoryCacheOptions()), NullLogger<MrWhoAuthorizationManager>.Instance);

        var context = await manager.BuildAuthorizeRequestAsync(new Uri("https://app/callback"));

        Assert.IsTrue(context.UsesRequestObject);
        Assert.IsFalse(string.IsNullOrEmpty(context.RequestObject));
    }

    [TestMethod]
    public async Task ValidateCallback_JarmResponseValidatesAndReturnsCode()
    {
    Span<byte> keyMaterial = stackalloc byte[32];
    RandomNumberGenerator.Fill(keyMaterial);
    var signingKey = new SymmetricSecurityKey(keyMaterial.ToArray()) { KeyId = "sig" };
        var options = new StaticOptionsMonitor(new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret",
            Jarm = { Enabled = true, ResponseMode = "query.jwt" }
        });

        var discovery = new StubDiscoveryClient(new MrWhoDiscoveryDocument
        {
            Issuer = "https://issuer.example.com",
            AuthorizationEndpoint = "https://issuer.example.com/authorize"
        });

    var jwks = new JsonWebKeySet();
        jwks.Keys.Add(JsonWebKeyConverter.ConvertFromSecurityKey(signingKey));

        var manager = new MrWhoAuthorizationManager(discovery, options, new StubJwksCache(jwks), new MemoryCache(new MemoryCacheOptions()), NullLogger<MrWhoAuthorizationManager>.Instance);

        var context = await manager.BuildAuthorizeRequestAsync(new Uri("https://app/callback"));

        var handler = new JsonWebTokenHandler();
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Issuer = options.CurrentValue.Issuer,
            Audience = options.CurrentValue.ClientId,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["code"] = "abc",
                ["state"] = context.State,
                ["c_hash"] = ComputeLeftHash("abc"),
                ["s_hash"] = ComputeLeftHash(context.State)
            },
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
        };
        var jarmToken = handler.CreateToken(tokenDescriptor);

        var result = await manager.ValidateCallbackAsync(context.State, null, null, jarmToken);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("abc", result.Code);
        Assert.IsTrue(result.IsJarmResponse);
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

    private static string ComputeLeftHash(string value)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(value));
        var left = new byte[hash.Length / 2];
    Array.Copy(hash, 0, left, 0, left.Length);
        return Base64UrlTextEncoder.Encode(left);
    }
}
