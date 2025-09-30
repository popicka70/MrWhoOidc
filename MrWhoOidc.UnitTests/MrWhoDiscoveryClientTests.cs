using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Discovery;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class MrWhoDiscoveryClientTests
{
    [TestMethod]
    public async Task GetAsync_UsesCacheAndETag()
    {
        var handler = new TestHandler();
        var factory = new StubHttpClientFactory(handler);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var options = new StaticOptionsMonitor(new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret",
            MetadataRefreshInterval = TimeSpan.FromMilliseconds(10)
        });

        var client = new MrWhoDiscoveryClient(factory, options, cache, NullLogger<MrWhoDiscoveryClient>.Instance);

        var doc1 = await client.GetAsync();
        Assert.AreEqual("https://issuer.example.com/.well-known/openid-configuration", handler.LastRequest?.RequestUri?.ToString());
        Assert.AreEqual(1, handler.RequestCount);
        Assert.AreEqual("https://issuer.example.com/token", doc1.TokenEndpoint);

        handler.NextStatusCode = System.Net.HttpStatusCode.NotModified;

    await Task.Delay(25);

        var doc2 = await client.GetAsync();
        Assert.AreEqual(2, handler.RequestCount);
        Assert.IsNotNull(handler.LastRequest);
        Assert.AreEqual("\"e1\"", handler.LastRequest!.Headers.IfNoneMatch.Single().Tag);
        Assert.AreSame(doc1, doc2);
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public System.Net.HttpStatusCode NextStatusCode { get; set; } = System.Net.HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;

            HttpResponseMessage response;
            if (NextStatusCode == System.Net.HttpStatusCode.OK)
            {
                var json = "{\"issuer\":\"https://issuer.example.com\",\"token_endpoint\":\"https://issuer.example.com/token\"}";
                response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e1\"");
            }
            else
            {
                response = new HttpResponseMessage(NextStatusCode);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _client = new HttpClient(handler);
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<MrWhoOidcClientOptions>
    {
        public StaticOptionsMonitor(MrWhoOidcClientOptions value)
        {
            CurrentValue = value;
        }

        public MrWhoOidcClientOptions CurrentValue { get; }

        public MrWhoOidcClientOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<MrWhoOidcClientOptions, string?> listener) => null;
    }
}
