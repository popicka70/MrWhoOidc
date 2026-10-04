using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers.Introspection;
using System.Text;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// RFC 6749 §5.2 / RFC 7662 §2.1: failed client authentication at /introspect is 401 invalid_client.
/// </summary>
[TestClass]
public sealed class IntrospectionClientAuthenticatorTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AuthenticateAsync_InvalidSecret_Returns401InvalidClient(bool useBasic)
    {
        var clientStore = new Mock<IClientStore>();
        clientStore.Setup(s => s.ValidateClientSecretAsync("rs", "wrong", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var authenticator = new ClientAuthenticator(
            clientStore.Object,
            new Mock<IClientAssertionValidator>().Object,
            Options.Create(new AuthOptions()),
            new Mock<IMtlsThumbprintResolver>().Object,
            NullLogger<ClientAuthenticator>.Instance);

        var http = new DefaultHttpContext();
        if (useBasic)
        {
            http.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("rs:wrong"));
        }

        var (authenticated, error) = await authenticator.AuthenticateAsync(new IntrospectionContext
        {
            Request = new IntrospectionRequest("token", null, "rs", "wrong", null, null),
            Client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "rs" },
            Issuer = "https://op.example.com",
            Endpoint = "https://op.example.com/introspect",
            HttpContext = http,
            ClientBucket = "bucket",
            MetricTags = []
        });

        Assert.IsFalse(authenticated);
        Assert.AreEqual(401, ((IStatusCodeHttpResult)error!).StatusCode);
        var payload = (Dictionary<string, object?>)((IValueHttpResult)error!).Value!;
        Assert.AreEqual("invalid_client", payload["error"]);
        Assert.AreEqual(useBasic, http.Response.Headers.WWWAuthenticate.ToString().StartsWith("Basic ", StringComparison.Ordinal));
    }
}
