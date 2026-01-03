using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;

namespace MrWhoOidc.UnitTests.Services.SubjectIdentifiers;

[TestClass]
public sealed class SectorIdentifierResolverTests
{
    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected exception of type {typeof(TException).Name}.");
    }

    [TestMethod]
    public async Task ResolveSectorIdentifierAsync_DerivesHost_FromAllowedRedirectUris()
    {
        var resolver = new SectorIdentifierResolver(new StubHttpClientFactory());

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[]
            {
                "https://app.example.com/signin-oidc",
                "https://app.example.com/callback"
            })
        };

        var sector = await resolver.ResolveSectorIdentifierAsync(client);

        Assert.AreEqual("app.example.com", sector);
    }

    [TestMethod]
    public async Task ResolveSectorIdentifierAsync_Throws_WhenMultipleHostsPresent()
    {
        var resolver = new SectorIdentifierResolver(new StubHttpClientFactory());

        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[]
            {
                "https://a.example.com/signin-oidc",
                "https://b.example.com/callback"
            })
        };

        await AssertThrowsAsync<InvalidOperationException>(() => resolver.ResolveSectorIdentifierAsync(client));
    }

    [TestMethod]
    public async Task ResolveSectorIdentifierAsync_Throws_WhenNoRedirectUrisConfigured()
    {
        var resolver = new SectorIdentifierResolver(new StubHttpClientFactory());
        var client = new MrWhoOidc.Auth.Persistence.Client { AllowedLoginRedirectUrisJson = null };

        await AssertThrowsAsync<InvalidOperationException>(() => resolver.ResolveSectorIdentifierAsync(client));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient(new ThrowingHandler());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => throw new InvalidOperationException("HTTP should not be used by these tests.");
    }
}
