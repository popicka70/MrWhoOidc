using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;

namespace MrWhoOidc.UnitTests.Services.SubjectIdentifiers;

[TestClass]
public sealed class SectorIdentifierUriValidatorTests
{
    [TestMethod]
    public async Task ValidateAsync_RequiresHttps()
    {
        var uri = new Uri("http://sector.example.com/redirect_uris.json");
        var http = new HttpClient(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        }));

        await AssertThrowsAsync<InvalidOperationException>(() =>
            SectorIdentifierUriValidator.ValidateAsync(uri, new[] { "https://app.example.com/cb" }, http));
    }

    [TestMethod]
    public async Task ValidateAsync_RejectsMissingRedirectUri()
    {
        var uri = new Uri("https://sector.example.com/redirect_uris.json");
        var http = new HttpClient(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[\"https://other.example.com/cb\"]", Encoding.UTF8, "application/json")
        }));

        await AssertThrowsAsync<InvalidOperationException>(() =>
            SectorIdentifierUriValidator.ValidateAsync(uri, new[] { "https://app.example.com/cb" }, http));
    }

    [TestMethod]
    public async Task ValidateAsync_AllowsWhenRedirectUriIncluded()
    {
        var uri = new Uri("https://sector.example.com/redirect_uris.json");
        var http = new HttpClient(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[\"https://app.example.com/cb\",\"https://other.example.com/cb\"]", Encoding.UTF8, "application/json")
        }));

        await SectorIdentifierUriValidator.ValidateAsync(uri, new[] { "https://app.example.com/cb" }, http);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
