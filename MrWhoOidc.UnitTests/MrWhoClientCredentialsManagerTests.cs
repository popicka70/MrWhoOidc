using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Client.Options;
using MrWhoOidc.Client.Tokens;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class MrWhoClientCredentialsManagerTests
{
    [TestMethod]
    public async Task AcquireTokenAsync_CachesSuccessfulResult()
    {
        var options = CreateOptions();
        options.ClientCredentials["service"] = new ClientCredentialsRegistration
        {
            Scopes = new[] { "api.read" },
            CacheLifetime = TimeSpan.FromMinutes(5)
        };

        var monitor = new StaticOptionsMonitor(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tokenClient = new Mock<IMrWhoTokenClient>(MockBehavior.Strict);

        var tokenResult = TokenResult.FromSuccess(new TokenResponsePayload("m2m-token", null, "Bearer", 3600, null, "api.read", "{}"));
        tokenClient.Setup(t => t.ClientCredentialsAsync(It.IsAny<ClientCredentialsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tokenResult);

        var manager = new MrWhoClientCredentialsManager(tokenClient.Object, monitor, cache, NullLogger<MrWhoClientCredentialsManager>.Instance);

        var first = await manager.AcquireTokenAsync("service", CancellationToken.None).ConfigureAwait(false);
        var second = await manager.AcquireTokenAsync("service", CancellationToken.None).ConfigureAwait(false);

        Assert.AreSame(tokenResult, first);
        Assert.AreSame(tokenResult, second);
        tokenClient.Verify(t => t.ClientCredentialsAsync(It.IsAny<ClientCredentialsRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task AcquireTokenAsync_ForceRefresh_BypassesCache()
    {
        var options = CreateOptions();
        options.ClientCredentials["service"] = new ClientCredentialsRegistration { Scopes = new[] { "api.read" }, CacheLifetime = TimeSpan.FromMinutes(5) };

        var monitor = new StaticOptionsMonitor(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tokenClient = new Mock<IMrWhoTokenClient>(MockBehavior.Strict);

        var tokenResult = TokenResult.FromSuccess(new TokenResponsePayload("m2m-token", null, "Bearer", 3600, null, "api.read", "{}"));
        tokenClient.Setup(t => t.ClientCredentialsAsync(It.IsAny<ClientCredentialsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tokenResult);

        var manager = new MrWhoClientCredentialsManager(tokenClient.Object, monitor, cache, NullLogger<MrWhoClientCredentialsManager>.Instance);

        await manager.AcquireTokenAsync("service", CancellationToken.None).ConfigureAwait(false);
        await manager.AcquireTokenAsync("service", CancellationToken.None, forceRefresh: true).ConfigureAwait(false);

        tokenClient.Verify(t => t.ClientCredentialsAsync(It.IsAny<ClientCredentialsRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task AcquireTokenAsync_PassesRegistrationParameters()
    {
        var options = CreateOptions();
        options.ClientCredentials["service"] = new ClientCredentialsRegistration
        {
            Audience = "api://resource",
            Resource = "https://resource",
            Scopes = new[] { "api.read" },
            AdditionalParameters = { ["custom"] = "value" }
        };

        var monitor = new StaticOptionsMonitor(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tokenClient = new Mock<IMrWhoTokenClient>(MockBehavior.Strict);

        var tokenResult = TokenResult.FromSuccess(new TokenResponsePayload("m2m-token", null, "Bearer", 3600, null, "api.read", "{}"));
        tokenClient.Setup(t => t.ClientCredentialsAsync(It.Is<ClientCredentialsRequest>(r =>
            r.Audience == "api://resource" &&
            r.Resource == "https://resource" &&
            r.Scopes != null && r.Scopes.Count == 1 &&
            r.AdditionalParameters.ContainsKey("custom")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(tokenResult);

        var manager = new MrWhoClientCredentialsManager(tokenClient.Object, monitor, cache, NullLogger<MrWhoClientCredentialsManager>.Instance);

        var result = await manager.AcquireTokenAsync("service", CancellationToken.None).ConfigureAwait(false);
        Assert.AreSame(tokenResult, result);
    }

    private static MrWhoOidcClientOptions CreateOptions() => new()
    {
        Issuer = "https://issuer.example.com",
        ClientId = "client",
        ClientSecret = "secret",
        Name = "default"
    };

    // Reuse the static options monitor from OBO tests via a nested type to avoid duplication.
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
