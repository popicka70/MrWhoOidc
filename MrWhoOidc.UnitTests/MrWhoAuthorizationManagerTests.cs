using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Authorization;
using MrWhoOidc.Client.Discovery;
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

        var manager = new MrWhoAuthorizationManager(discovery, options, new MemoryCache(new MemoryCacheOptions()), NullLogger<MrWhoAuthorizationManager>.Instance);

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
        var manager = new MrWhoAuthorizationManager(discovery, options, new MemoryCache(new MemoryCacheOptions()), NullLogger<MrWhoAuthorizationManager>.Instance);

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
        var manager = new MrWhoAuthorizationManager(discovery, options, new MemoryCache(new MemoryCacheOptions()), NullLogger<MrWhoAuthorizationManager>.Instance);

        var context = await manager.BuildAuthorizeRequestAsync(new Uri("https://app/callback"));
        var result = await manager.ValidateCallbackAsync(context.State, "code", null);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("code", result.Code);
        Assert.AreEqual(context.CodeVerifier, result.CodeVerifier);
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
}
