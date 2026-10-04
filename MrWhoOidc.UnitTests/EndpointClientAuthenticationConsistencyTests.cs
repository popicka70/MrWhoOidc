using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Observability;
using System.Text;
using PersistedClient = MrWhoOidc.Auth.Persistence.Client;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// C1 residue: /par, /revoke, /bc-authorize and /device/authorize authenticate clients through the shared
/// authenticator, so they enforce the same rules as /token: exactly one authentication method per request
/// (RFC 6749 §2.3) and the client's registered token_endpoint_auth_method (RFC 7591).
/// /introspect is covered in <see cref="IntrospectionClientAuthenticatorTests"/>.
/// The secret is always "valid" here, so a rejection proves the method rules, not the secret check.
/// </summary>
[TestClass]
public sealed class EndpointClientAuthenticationConsistencyTests
{
    private const string Issuer = "https://test.example.com";
    private static readonly Guid TenantId = Guid.NewGuid();

    public enum Endpoint { Par, Revoke, Ciba, Device }

    private static Mock<IClientStore> ClientStore(PersistedClient client)
    {
        var store = new Mock<IClientStore>();
        store.Setup(s => s.FindByClientIdAsync(client.ClientId, It.IsAny<CancellationToken>())).ReturnsAsync(client);
        store.Setup(s => s.ValidateClientSecretAsync(client.ClientId, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return store;
    }

    private static IClientAssertionValidator AcceptingAssertions()
    {
        var v = new Mock<IClientAssertionValidator>();
        v.Setup(x => x.ValidateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        v.Setup(x => x.ValidateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return v.Object;
    }

    private static PersistedClient NewClient(string? method) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantId,
        ClientId = "c1",
        TokenEndpointAuthMethod = method,
        AllowCiba = true,
        AllowDeviceAuthorization = true,
    };

    private static DefaultHttpContext Http(string path, Dictionary<string, string> form, string? basic = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<MrWhoOidc.Auth.MultiTenancy.ITenantAccessor, MrWhoOidc.Auth.MultiTenancy.TenantAccessor>();
        services.AddSingleton<MrWhoOidc.Auth.MultiTenancy.IMultiTenancyOptions>(new MrWhoOidc.Auth.MultiTenancy.MultiTenancyOptions());
        services.AddScoped<MrWhoOidc.Auth.MultiTenancy.IIssuerBuilder, MrWhoOidc.Auth.MultiTenancy.IssuerBuilder>();
        services.AddSingleton(new OidcOptions { Issuer = Issuer });

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("test.example.com");
        http.Request.Path = path;
        http.Request.Method = "POST";
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Form = new FormCollection(form.ToDictionary(kv => kv.Key, kv => new Microsoft.Extensions.Primitives.StringValues(kv.Value)));
        http.Response.Body = new MemoryStream();
        if (basic is not null)
        {
            http.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(basic));
        }
        return http;
    }

    private static async Task<int> InvokeAsync(Endpoint endpoint, PersistedClient client, Dictionary<string, string> form, string? basic = null)
    {
        var store = ClientStore(client).Object;
        var assertions = AcceptingAssertions();
        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        tenantAccessor.SetTenant(new MrWhoOidc.Auth.MultiTenancy.TenantContext { TenantId = TenantId, Slug = "test", IssuerUri = Issuer });

        var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        IResult result;
        DefaultHttpContext http;
        switch (endpoint)
        {
            case Endpoint.Par:
                form = new(form) { ["response_type"] = "code", ["redirect_uri"] = "https://app.example.com/cb", ["scope"] = "openid" };
                http = Http("/par", form, basic);
                // Downstream request validation rejects (400) so a successful client authentication stays observable.
                var authorize = new Mock<IAuthorizeRequestValidator>();
                authorize.Setup(a => a.ValidateAsync(It.IsAny<AuthorizeRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AuthorizeValidationResult(false, "invalid_request", "stop"));
                result = await new ParHandler(new OidcOptions { Issuer = Issuer }, store, assertions,
                    authorize.Object, new Mock<IPushedAuthorizationRequestStore>().Object,
                    new Mock<IRequestObjectValidator>().Object, Options.Create(new AuthOptions()), new OidcEndpointMetrics(),
                    NullLogger<ParHandler>.Instance).HandleAsync(http);
                break;
            case Endpoint.Revoke:
                form = new(form) { ["token"] = "some-token" };
                http = Http("/revoke", form, basic);
                result = await new RevocationHandler(new Mock<IRevocationService>().Object, store, new NoopAuditSink(),
                    new OidcEndpointMetrics(), assertions, Options.Create(new AuthOptions()), new MtlsThumbprintResolver(),
                    new OidcOptions { Issuer = Issuer }).HandleAsync(http);
                break;
            case Endpoint.Ciba:
                form = new(form) { ["login_hint"] = "user@example.com", ["scope"] = "openid" };
                http = Http("/bc-authorize", form, basic);
                result = await new CibaAuthenticationHandler(new OidcOptions { Issuer = Issuer },
                    Options.Create(new AuthOptions { EnableCiba = true }), db, store, assertions,
                    new Mock<ITokenValidator>().Object, tenantAccessor, new Mock<ICibaNotificationService>().Object,
                    NullLogger<CibaAuthenticationHandler>.Instance).HandleAsync(http);
                break;
            default:
                form = new(form) { ["scope"] = "openid" };
                http = Http("/device/authorize", form, basic);
                result = await new DeviceAuthorizationHandler(new OidcOptions { Issuer = Issuer },
                    Options.Create(new AuthOptions { EnableDeviceAuthorizationGrant = true }), db, store, assertions,
                    tenantAccessor, NullLogger<DeviceAuthorizationHandler>.Instance).HandleAsync(http);
                break;
        }

        await result.ExecuteAsync(http);
        return http.Response.StatusCode;
    }

    [TestMethod]
    [DataRow(Endpoint.Par)]
    [DataRow(Endpoint.Revoke)]
    [DataRow(Endpoint.Ciba)]
    [DataRow(Endpoint.Device)]
    public async Task PrivateKeyJwtClient_PresentingLeftoverSecret_IsRejected(Endpoint endpoint)
    {
        var status = await InvokeAsync(endpoint, NewClient("private_key_jwt"),
            new() { ["client_id"] = "c1", ["client_secret"] = "leftover" });

        Assert.AreEqual(401, status);
    }

    [TestMethod]
    [DataRow(Endpoint.Par)]
    [DataRow(Endpoint.Revoke)]
    [DataRow(Endpoint.Ciba)]
    [DataRow(Endpoint.Device)]
    public async Task PrivateKeyJwtClient_PresentingAssertionAndSecret_IsRejected(Endpoint endpoint)
    {
        var status = await InvokeAsync(endpoint, NewClient("private_key_jwt"), new()
        {
            ["client_id"] = "c1",
            ["client_secret"] = "leftover",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = "a.b.c",
        });

        Assert.AreEqual(401, status);
    }

    [TestMethod]
    [DataRow(Endpoint.Par)]
    [DataRow(Endpoint.Revoke)]
    [DataRow(Endpoint.Ciba)]
    [DataRow(Endpoint.Device)]
    public async Task BasicAndFormSecret_BothPresent_IsRejected(Endpoint endpoint)
    {
        var status = await InvokeAsync(endpoint, NewClient(null),
            new() { ["client_id"] = "c1", ["client_secret"] = "s3cret" }, basic: "c1:s3cret");

        Assert.AreEqual(401, status);
    }

    [TestMethod]
    [DataRow(Endpoint.Par)]
    [DataRow(Endpoint.Revoke)]
    [DataRow(Endpoint.Ciba)]
    [DataRow(Endpoint.Device)]
    public async Task ClientSecretPostClient_UsingBasic_IsRejected(Endpoint endpoint)
    {
        var status = await InvokeAsync(endpoint, NewClient("client_secret_post"), new(), basic: "c1:s3cret");

        Assert.AreEqual(401, status);
    }

    [TestMethod]
    [DataRow(Endpoint.Par)]
    [DataRow(Endpoint.Revoke)]
    [DataRow(Endpoint.Ciba)]
    [DataRow(Endpoint.Device)]
    public async Task PrivateKeyJwtClient_PresentingOnlyAssertion_IsAuthenticated(Endpoint endpoint)
    {
        var status = await InvokeAsync(endpoint, NewClient("private_key_jwt"), new()
        {
            ["client_id"] = "c1",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = "a.b.c",
        });

        // Passes client authentication; later request validation may still fail with 400 (e.g. unknown CIBA user).
        Assert.AreNotEqual(401, status);
    }

    [TestMethod]
    [DataRow(Endpoint.Par, true)]
    [DataRow(Endpoint.Revoke, true)]
    [DataRow(Endpoint.Device, true)]
    [DataRow(Endpoint.Ciba, false)]
    public async Task PublicClient_AllowedOnlyWhereEndpointPermits(Endpoint endpoint, bool allowed)
    {
        var status = await InvokeAsync(endpoint, NewClient("none"), new() { ["client_id"] = "c1" });

        Assert.AreEqual(allowed, status != 401, $"status {status}");
    }
}
