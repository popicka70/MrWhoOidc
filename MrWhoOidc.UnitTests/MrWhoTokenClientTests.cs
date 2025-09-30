using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Discovery;
using MrWhoOidc.Client.Options;
using MrWhoOidc.Client.Tokens;
using MrWhoOidc.Security;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class MrWhoTokenClientTests
{
    [TestMethod]
    public async Task ExchangeCode_Succeeds()
    {
        var handler = new CapturingHandler();
        var factory = new StubHttpClientFactory(handler);
        var options = new StaticOptionsMonitor(new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret"
        });
        var discovery = new StubDiscoveryClient(new MrWhoDiscoveryDocument
        {
            Issuer = "https://issuer.example.com",
            TokenEndpoint = "https://issuer.example.com/token",
            AuthorizationEndpoint = "https://issuer.example.com/authorize"
        });

        handler.ResponseContent = JsonSerializer.Serialize(new
        {
            access_token = "access",
            token_type = "Bearer",
            expires_in = 3600,
            scope = "openid profile"
        });

        var client = new MrWhoTokenClient(factory, discovery, options, new StubDpopGenerator(), NullLogger<MrWhoTokenClient>.Instance);

        var result = await client.ExchangeCodeAsync("code123", new Uri("https://app/callback"), "verifier");

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("access", result.AccessToken);
        Assert.IsNotNull(handler.LastRequest);
        var auth = handler.LastRequest!.Headers.Authorization;
        Assert.IsNotNull(auth);
        Assert.AreEqual("Basic", auth!.Scheme);
        Assert.AreEqual("authorization_code", handler.FormValues["grant_type"]);
        Assert.AreEqual("verifier", handler.FormValues["code_verifier"]);
    }

    [TestMethod]
    public async Task ErrorResponse_ReturnsError()
    {
        var handler = new CapturingHandler
        {
            ResponseStatusCode = System.Net.HttpStatusCode.BadRequest,
            ResponseContent = "{\"error\":\"invalid_grant\"}"
        };

        var factory = new StubHttpClientFactory(handler);
        var options = new StaticOptionsMonitor(new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret"
        });
        var discovery = new StubDiscoveryClient(new MrWhoDiscoveryDocument
        {
            Issuer = "https://issuer.example.com",
            TokenEndpoint = "https://issuer.example.com/token"
        });

        var client = new MrWhoTokenClient(factory, discovery, options, new StubDpopGenerator(), NullLogger<MrWhoTokenClient>.Instance);

        var result = await client.RefreshTokenAsync("refresh");

        Assert.IsTrue(result.IsError);
        Assert.AreEqual("invalid_grant", result.Error);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public Dictionary<string, string> FormValues { get; } = new(StringComparer.Ordinal);
        public string ResponseContent { get; set; } = "{}";
        public System.Net.HttpStatusCode ResponseStatusCode { get; set; } = System.Net.HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is FormUrlEncodedContent form)
            {
                var payload = await form.ReadAsStringAsync(cancellationToken);
                foreach (var kv in payload.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = kv.Split('=');
                    if (parts.Length == 2)
                    {
                        FormValues[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
                    }
                }
            }

            return new HttpResponseMessage(ResponseStatusCode)
            {
                Content = new StringContent(ResponseContent)
            };
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

    private sealed class StubDpopGenerator : IDPoPProofGenerator
    {
        public ValueTask<string> CreateProofAsync(DPoPProofRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult("proof");
    }
}
