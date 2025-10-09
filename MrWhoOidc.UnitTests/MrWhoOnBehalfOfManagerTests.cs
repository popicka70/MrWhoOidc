using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Client.Options;
using MrWhoOidc.Client.Tokens;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class MrWhoOnBehalfOfManagerTests
{
    [TestMethod]
    public async Task AcquireTokenAsync_CachesSuccessfulResult()
    {
        var options = CreateOptions();
        options.OnBehalfOf["api"] = new OnBehalfOfRegistration { Scope = "api.read", CacheLifetime = TimeSpan.FromMinutes(10) };

        var monitor = new StaticOptionsMonitor(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tokenClient = new Mock<IMrWhoTokenClient>(MockBehavior.Strict);

        var tokenResult = TokenResult.FromSuccess(new TokenResponsePayload("obo-token", null, "Bearer", 3600, null, "api.read", "{}"));
        tokenClient.Setup(t => t.TokenExchangeAsync(It.IsAny<TokenExchangeRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(tokenResult);

        var manager = new MrWhoOnBehalfOfManager(tokenClient.Object, monitor, cache, NullLogger<MrWhoOnBehalfOfManager>.Instance);

        var first = await manager.AcquireTokenAsync("api", "user-token", CancellationToken.None).ConfigureAwait(false);
        var second = await manager.AcquireTokenAsync("api", "user-token", CancellationToken.None).ConfigureAwait(false);

        Assert.AreSame(tokenResult, first);
        Assert.AreSame(tokenResult, second);
        tokenClient.Verify(t => t.TokenExchangeAsync(It.IsAny<TokenExchangeRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task AcquireTokenAsync_DoesNotCacheErrors()
    {
        var options = CreateOptions();
        options.OnBehalfOf["api"] = new OnBehalfOfRegistration { Scope = "api.read" };

        var monitor = new StaticOptionsMonitor(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tokenClient = new Mock<IMrWhoTokenClient>(MockBehavior.Strict);

        var error = TokenResult.FromError("invalid_request", "boom", "{}");
        tokenClient.Setup(t => t.TokenExchangeAsync(It.IsAny<TokenExchangeRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(error);

        var manager = new MrWhoOnBehalfOfManager(tokenClient.Object, monitor, cache, NullLogger<MrWhoOnBehalfOfManager>.Instance);

        var first = await manager.AcquireTokenAsync("api", "user-token", CancellationToken.None).ConfigureAwait(false);
        var second = await manager.AcquireTokenAsync("api", "user-token", CancellationToken.None).ConfigureAwait(false);

        Assert.AreSame(error, first);
        Assert.AreSame(error, second);
        tokenClient.Verify(t => t.TokenExchangeAsync(It.IsAny<TokenExchangeRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task AcquireTokenAsync_PassesRegistrationParameters()
    {
        var options = CreateOptions();
        options.OnBehalfOf["api"] = new OnBehalfOfRegistration
        {
            Audience = "api://resource",
            Resource = "https://resource",
            Scope = "api.read",
            RequestedTokenType = "urn:ietf:params:oauth:token-type:access_token",
            AdditionalParameters = { ["actor_token"] = "actor" }
        };

        var monitor = new StaticOptionsMonitor(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tokenClient = new Mock<IMrWhoTokenClient>(MockBehavior.Strict);

        var tokenResult = TokenResult.FromSuccess(new TokenResponsePayload("obo-token", null, "Bearer", 3600, null, "api.read", "{}"));
        tokenClient.Setup(t => t.TokenExchangeAsync(It.Is<TokenExchangeRequest>(r =>
                r.SubjectToken == "subject-token" &&
                r.Audience == "api://resource" &&
                r.Resource == "https://resource" &&
                r.Scope == "api.read" &&
                r.AdditionalParameters.ContainsKey("actor_token")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tokenResult);

        var manager = new MrWhoOnBehalfOfManager(tokenClient.Object, monitor, cache, NullLogger<MrWhoOnBehalfOfManager>.Instance);

        var result = await manager.AcquireTokenAsync("api", "subject-token", CancellationToken.None).ConfigureAwait(false);
        Assert.AreSame(tokenResult, result);
    }

    private static MrWhoOidcClientOptions CreateOptions() => new()
    {
        Issuer = "https://issuer.example.com",
        ClientId = "client",
        ClientSecret = "secret",
        Name = "default"
    };

    private sealed class StaticOptionsMonitor : IOptionsMonitor<MrWhoOidcClientOptions>
    {
        private readonly MrWhoOidcClientOptions _value;

        public StaticOptionsMonitor(MrWhoOidcClientOptions value)
        {
            _value = value;
        }

        public MrWhoOidcClientOptions CurrentValue => _value;

        public MrWhoOidcClientOptions Get(string? name) => _value;

        public IDisposable OnChange(Action<MrWhoOidcClientOptions, string?> listener) => EmptyDisposable.Instance;

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
