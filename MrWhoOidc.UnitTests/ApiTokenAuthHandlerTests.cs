using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.WebAuth.Security.ApiBearer;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class ApiTokenAuthHandlerTests
{
    private const string Issuer = "https://mrwho.onrender.com/t/default";
    private static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static Claim[] AdminTokenClaims(string sub = "user-123", string clientId = "mrwho-cli-default") =>
    [
        new("sub", sub),
        new("client_id", clientId),
        new("aud", AdminApiAccess.Resource),
        new("scope", $"openid {AdminApiAccess.Scope}"),
    ];

    private static MrWhoOidc.Auth.Persistence.Client CliClient(bool allowAdminApi = true) => new()
    {
        ClientId = "mrwho-cli-default",
        TenantId = DefaultTenantId,
        IsSystemClient = true,
        AllowAdminApi = allowAdminApi,
    };

    private static async Task<(AuthenticateResult Result, CapturingTokenValidator Validator, RecordingTenantResolver Resolver)> AuthenticateAsync(
        Claim[] validatedClaims,
        string path = "/platform-admin/api/clients",
        string? typ = "at+jwt",
        string tokenIssuer = Issuer,
        MrWhoOidc.Auth.Persistence.Client? client = null,
        bool acceptLegacy = false)
    {
        var tenantAccessor = new TenantAccessor();
        var resolver = new RecordingTenantResolver();
        var validator = new CapturingTokenValidator(tenantAccessor, validatedClaims);
        var clients = new Mock<IClientStore>();
        clients.Setup(c => c.FindByClientIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => client is not null && client.ClientId == id ? client : null);
        var defaultTenant = new Mock<IDefaultTenantContext>();
        defaultTenant.Setup(d => d.GetDefaultTenantIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(DefaultTenantId);

        var handler = new ApiTokenAuthHandler(
            new TestOptionsMonitor<ApiTokenAuthOptions>(new ApiTokenAuthOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            validator,
            Options.Create(new AuthOptions { ApiAudiences = ["api"], AdminApiAcceptLegacyTokens = acceptLegacy }),
            resolver,
            tenantAccessor,
            clients.Object,
            defaultTenant.Object);

        var services = new ServiceCollection()
            .AddSingleton<ITenantAccessor>(tenantAccessor)
            .AddSingleton(new OidcOptions { Issuer = Issuer })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Request.Headers.Authorization = $"Bearer {CreateUnsignedToken(tokenIssuer, typ)}";

        await handler.InitializeAsync(new AuthenticationScheme(ApiTokenAuthHandler.SchemeName, null, typeof(ApiTokenAuthHandler)), context);
        return (await handler.AuthenticateAsync(), validator, resolver);
    }

    [TestMethod]
    public async Task AdminToken_FromAnAdminClient_Authenticates_AndMapsSub()
    {
        var (result, validator, resolver) = await AuthenticateAsync(AdminTokenClaims(), client: CliClient());

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.AreEqual("/t/default", resolver.LastResolvedPath);
        Assert.AreEqual("default", validator.ObservedTenant!.Slug);
        Assert.AreEqual("user-123", result.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        CollectionAssert.AreEqual(new[] { AdminApiAccess.Resource }, validator.ObservedAudiences, "only the admin resource is a valid audience");
        Assert.AreEqual(Issuer, validator.ObservedIssuer, "the expected issuer comes from the tenant, not from the token");
    }

    // H3 / ADR-0010: any RP access token (aud=api) of an admin used to work on the admin APIs.

    [TestMethod]
    public async Task OrdinaryRpToken_IsRejected_ByDefault()
    {
        Claim[] rpToken = [new("sub", "user-123"), new("client_id", "some-rp"), new("aud", "api"), new("scope", "openid")];

        var (result, validator, _) = await AuthenticateAsync(rpToken);

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.DoesNotContain(validator.ObservedAudiences!, "api", "aud=api is a valid audience only when legacy tokens are enabled");
    }

    [TestMethod]
    public async Task OrdinaryRpToken_IsAccepted_OnlyWhenLegacyTokensAreEnabled()
    {
        Claim[] rpToken = [new("sub", "user-123"), new("client_id", "some-rp"), new("aud", "api"), new("scope", "openid")];

        var (result, _, _) = await AuthenticateAsync(rpToken, acceptLegacy: true);

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
    }

    [TestMethod]
    public async Task AdminToken_WithoutAdminScope_IsRejected()
    {
        Claim[] claims = [new("sub", "user-123"), new("client_id", "mrwho-cli-default"), new("aud", AdminApiAccess.Resource), new("scope", "openid")];

        Assert.IsFalse((await AuthenticateAsync(claims, client: CliClient())).Result.Succeeded);
    }

    [TestMethod]
    public async Task AdminToken_FromAClientWithoutAllowAdminApi_IsRejected()
    {
        Assert.IsFalse((await AuthenticateAsync(AdminTokenClaims(), client: CliClient(allowAdminApi: false))).Result.Succeeded);
    }

    [TestMethod]
    public async Task AdminToken_FromAnUnknownClient_IsRejected()
    {
        Assert.IsFalse((await AuthenticateAsync(AdminTokenClaims(clientId: "unknown"), client: CliClient())).Result.Succeeded);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("JWT")]
    [DataRow("logout+jwt")]
    public async Task NonAccessTokenTypes_AreRejected(string? typ)
    {
        Assert.IsFalse((await AuthenticateAsync(AdminTokenClaims(), typ: typ, client: CliClient())).Result.Succeeded, $"typ={typ ?? "(none)"}");
    }

    [TestMethod]
    public async Task TokenFromAnotherTenant_IsRejected_OnPlatformRoutes()
    {
        var (result, _, _) = await AuthenticateAsync(AdminTokenClaims(), tokenIssuer: "https://mrwho.onrender.com/t/acme", client: CliClient());

        Assert.IsFalse(result.Succeeded, "a non-platform tenant's token must not act on /platform-admin");
    }

    [TestMethod]
    public async Task ClientCredentialsTokenNamedAfterAUser_IsRejected()
    {
        // V1: a tenant admin named a client after the platform admin's user id and used its client_credentials
        // token (sub = client_id) on /platform-admin/api.
        var platformAdminUserId = Guid.NewGuid().ToString();

        var (result, _, _) = await AuthenticateAsync(AdminTokenClaims(sub: platformAdminUserId, clientId: platformAdminUserId), acceptLegacy: true);

        Assert.IsFalse(result.Succeeded, "a client token must never authenticate as a user");
        Assert.IsNull(result.Principal);
    }

    private static string CreateUnsignedToken(string issuer, string? typ)
    {
        var header = new JwtHeader();
        header.Remove("typ");
        if (typ is not null)
        {
            header["typ"] = typ;
        }

        var payload = new JwtPayload(issuer, AdminApiAccess.Resource, [new Claim("sub", "user-123")], null, DateTime.UtcNow.AddMinutes(5));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private sealed class RecordingTenantResolver : ITenantResolver
    {
        public string? LastResolvedPath { get; private set; }

        public Task<TenantContext?> ResolveTenantAsync(string path, CancellationToken cancellationToken = default)
        {
            LastResolvedPath = path;
            return Task.FromResult<TenantContext?>(path.ToLowerInvariant() switch
            {
                "/t/default" => new TenantContext { TenantId = DefaultTenantId, Slug = "default", Name = "Default Tenant", IssuerUri = Issuer, IsMultiTenantMode = true },
                "/t/acme" => new TenantContext { TenantId = Guid.NewGuid(), Slug = "acme", Name = "Acme", IssuerUri = "https://mrwho.onrender.com/t/acme", IsMultiTenantMode = true },
                _ => null,
            });
        }
    }

    private sealed class CapturingTokenValidator(ITenantAccessor tenantAccessor, Claim[] claims) : ITokenValidator
    {
        public TenantContext? ObservedTenant { get; private set; }
        public string? ObservedIssuer { get; private set; }
        public string[]? ObservedAudiences { get; private set; }

        public Task<(bool ok, ClaimsPrincipal? principal, string? error)> ValidateAsync(
            string token,
            string issuer,
            CancellationToken ct = default,
            IEnumerable<string>? validAudiences = null,
            bool skipAudienceValidation = false)
        {
            ObservedTenant = tenantAccessor.CurrentTenant;
            ObservedIssuer = issuer;
            ObservedAudiences = validAudiences?.ToArray();

            // Behave like the real validator: the token's aud must be one of the valid audiences.
            var aud = claims.Where(c => c.Type == "aud").Select(c => c.Value);
            if (ObservedAudiences is null || !aud.Any(ObservedAudiences.Contains))
            {
                return Task.FromResult<(bool ok, ClaimsPrincipal? principal, string? error)>((false, null, "audience"));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiTokenAuthHandler.SchemeName));
            return Task.FromResult<(bool ok, ClaimsPrincipal? principal, string? error)>((true, principal, null));
        }
    }

    private sealed class TestOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;

        public T Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
