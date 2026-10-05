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
        clientStore.Setup(s => s.FindByClientIdAsync("rs", It.IsAny<CancellationToken>())).ReturnsAsync(new MrWhoOidc.Auth.Persistence.Client { ClientId = "rs" });
        clientStore.Setup(s => s.ValidateClientSecretAsync("rs", "wrong", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var authenticator = CreateAuthenticator(clientStore.Object);

        var form = new Dictionary<string, string> { ["token"] = "token" };
        if (!useBasic)
        {
            form["client_id"] = "rs";
            form["client_secret"] = "wrong";
        }
        var http = FormContext(form, useBasic ? "rs:wrong" : null);

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

    private static ClientAuthenticator CreateAuthenticator(IClientStore clientStore, IClientAssertionValidator? assertions = null)
        => new(
            MrWhoOidc.WebAuth.Services.ClientAuthenticator.Compose(
                clientStore,
                assertions ?? new Mock<IClientAssertionValidator>().Object,
                Options.Create(new AuthOptions())),
            NullLogger<ClientAuthenticator>.Instance);

    private static DefaultHttpContext FormContext(Dictionary<string, string> form, string? basic = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Form = new FormCollection(form.ToDictionary(kv => kv.Key, kv => new Microsoft.Extensions.Primitives.StringValues(kv.Value)));
        if (basic is not null)
        {
            http.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(basic));
        }
        return http;
    }

    private static IntrospectionContext Context(HttpContext http, string clientId, string? secret = null, string? assertion = null) => new()
    {
        Request = new IntrospectionRequest("token", null, clientId, secret, assertion is null ? null : "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", assertion),
        Client = new MrWhoOidc.Auth.Persistence.Client { ClientId = clientId },
        Issuer = "https://op.example.com",
        Endpoint = "https://op.example.com/introspect",
        HttpContext = http,
        ClientBucket = "bucket",
        MetricTags = []
    };

    // C1 residue: /introspect enforces the registered token_endpoint_auth_method and the single-method rule.
    [TestMethod]
    public async Task AuthenticateAsync_PrivateKeyJwtClient_WithLeftoverSecret_Rejected()
    {
        var client = new MrWhoOidc.Auth.Persistence.Client { ClientId = "rs", TokenEndpointAuthMethod = "private_key_jwt" };
        var clientStore = new Mock<IClientStore>();
        clientStore.Setup(s => s.FindByClientIdAsync("rs", It.IsAny<CancellationToken>())).ReturnsAsync(client);
        clientStore.Setup(s => s.ValidateClientSecretAsync("rs", "leftover", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var authenticator = CreateAuthenticator(clientStore.Object);

        var http = FormContext(new() { ["token"] = "token", ["client_id"] = "rs", ["client_secret"] = "leftover" });
        var (authenticated, error) = await authenticator.AuthenticateAsync(Context(http, "rs", "leftover"));

        Assert.IsFalse(authenticated);
        Assert.AreEqual(401, ((IStatusCodeHttpResult)error!).StatusCode);
    }

    [TestMethod]
    public async Task AuthenticateAsync_BasicAndFormSecret_Rejected()
    {
        var clientStore = new Mock<IClientStore>();
        clientStore.Setup(s => s.FindByClientIdAsync("rs", It.IsAny<CancellationToken>())).ReturnsAsync(new MrWhoOidc.Auth.Persistence.Client { ClientId = "rs" });
        clientStore.Setup(s => s.ValidateClientSecretAsync("rs", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var authenticator = CreateAuthenticator(clientStore.Object);

        var http = FormContext(new() { ["token"] = "token", ["client_id"] = "rs", ["client_secret"] = "s3cret" }, "rs:s3cret");
        var (authenticated, error) = await authenticator.AuthenticateAsync(Context(http, "rs", "s3cret"));

        Assert.IsFalse(authenticated);
        Assert.AreEqual(401, ((IStatusCodeHttpResult)error!).StatusCode);
    }

    [TestMethod]
    public async Task AuthenticateAsync_PublicClient_Rejected()
    {
        var clientStore = new Mock<IClientStore>();
        clientStore.Setup(s => s.FindByClientIdAsync("spa", It.IsAny<CancellationToken>())).ReturnsAsync(new MrWhoOidc.Auth.Persistence.Client { ClientId = "spa", TokenEndpointAuthMethod = "none" });
        clientStore.Setup(s => s.ValidateClientSecretAsync("spa", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var authenticator = CreateAuthenticator(clientStore.Object);

        var http = FormContext(new() { ["token"] = "token", ["client_id"] = "spa" });
        var (authenticated, _) = await authenticator.AuthenticateAsync(Context(http, "spa"));

        Assert.IsFalse(authenticated);
    }

    [TestMethod]
    public async Task AuthenticateAsync_ConfidentialClient_WithRegisteredMethod_Succeeds()
    {
        var clientStore = new Mock<IClientStore>();
        clientStore.Setup(s => s.FindByClientIdAsync("rs", It.IsAny<CancellationToken>())).ReturnsAsync(new MrWhoOidc.Auth.Persistence.Client { ClientId = "rs", TokenEndpointAuthMethod = "client_secret_basic" });
        clientStore.Setup(s => s.ValidateClientSecretAsync("rs", "s3cret", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var authenticator = CreateAuthenticator(clientStore.Object);

        var http = FormContext(new() { ["token"] = "token" }, "rs:s3cret");
        var (authenticated, _) = await authenticator.AuthenticateAsync(Context(http, "rs", "s3cret"));

        Assert.IsTrue(authenticated);
    }
}
