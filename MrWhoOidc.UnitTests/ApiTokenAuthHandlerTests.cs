using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Security.ApiBearer;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class ApiTokenAuthHandlerTests
{
    [TestMethod]
    public async Task AuthenticateAsync_ResolvesTenantContextFromIssuer_ForPlatformAdminBearerRequests()
    {
        var tenantAccessor = new TenantAccessor();
        var tenantResolver = new RecordingTenantResolver();
        var tokenValidator = new CapturingTokenValidator(tenantAccessor);
        var handler = new ApiTokenAuthHandler(
            new TestOptionsMonitor<ApiTokenAuthOptions>(new ApiTokenAuthOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            tokenValidator,
            Options.Create(new AuthOptions { ApiAudiences = ["api"] }),
            tenantResolver,
            tenantAccessor);

        var context = new DefaultHttpContext();
        context.Request.Path = "/platform-admin/api/clients";
        context.Request.Headers.Authorization = $"Bearer {CreateUnsignedToken("https://mrwho.onrender.com/t/default")}";

        await handler.InitializeAsync(
            new AuthenticationScheme(ApiTokenAuthHandler.SchemeName, null, typeof(ApiTokenAuthHandler)),
            context);

        var result = await handler.AuthenticateAsync();

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.AreEqual("/t/default", tenantResolver.LastResolvedPath);
        Assert.IsNotNull(tokenValidator.ObservedTenant);
        Assert.AreEqual("default", tokenValidator.ObservedTenant!.Slug);
        Assert.AreEqual(
            "user-123",
            result.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            "The handler should still map sub to NameIdentifier after bearer validation succeeds.");
    }

    [TestMethod]
    public async Task AuthenticateAsync_ClientCredentialsTokenNamedAfterAUser_IsRejected()
    {
        // V1: a tenant admin named a client after the platform admin's user id and used its client_credentials
        // token (sub = client_id) on /platform-admin/api.
        var platformAdminUserId = Guid.NewGuid().ToString();
        var tenantAccessor = new TenantAccessor();
        var handler = new ApiTokenAuthHandler(
            new TestOptionsMonitor<ApiTokenAuthOptions>(new ApiTokenAuthOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            new CapturingTokenValidator(tenantAccessor, [new Claim("sub", platformAdminUserId), new Claim("client_id", platformAdminUserId)]),
            Options.Create(new AuthOptions { ApiAudiences = ["api"] }),
            new RecordingTenantResolver(),
            tenantAccessor);

        var context = new DefaultHttpContext();
        context.Request.Path = "/platform-admin/api/tenants";
        context.Request.Headers.Authorization = $"Bearer {CreateUnsignedToken("https://mrwho.onrender.com/t/default")}";
        await handler.InitializeAsync(
            new AuthenticationScheme(ApiTokenAuthHandler.SchemeName, null, typeof(ApiTokenAuthHandler)),
            context);

        var result = await handler.AuthenticateAsync();

        Assert.IsFalse(result.Succeeded, "a client token must never authenticate as a user");
        Assert.IsNull(result.Principal);
    }

    [TestMethod]
    public async Task AuthenticateAsync_UserTokenIssuedToAClient_StillAuthenticates()
    {
        var tenantAccessor = new TenantAccessor();
        var handler = new ApiTokenAuthHandler(
            new TestOptionsMonitor<ApiTokenAuthOptions>(new ApiTokenAuthOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            new CapturingTokenValidator(tenantAccessor, [new Claim("sub", "user-123"), new Claim("client_id", "mrwho-cli")]),
            Options.Create(new AuthOptions { ApiAudiences = ["api"] }),
            new RecordingTenantResolver(),
            tenantAccessor);

        var context = new DefaultHttpContext();
        context.Request.Path = "/platform-admin/api/tenants";
        context.Request.Headers.Authorization = $"Bearer {CreateUnsignedToken("https://mrwho.onrender.com/t/default")}";
        await handler.InitializeAsync(
            new AuthenticationScheme(ApiTokenAuthHandler.SchemeName, null, typeof(ApiTokenAuthHandler)),
            context);

        var result = await handler.AuthenticateAsync();

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.AreEqual("user-123", result.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }

    [TestMethod]
    [DataRow("6f9619ff-8b86-d011-b42d-00c04fc964ff", true)]
    [DataRow("6f9619ff8b86d011b42d00c04fc964ff", true)]
    [DataRow("{6f9619ff-8b86-d011-b42d-00c04fc964ff}", true)]
    [DataRow(" 6f9619ff-8b86-d011-b42d-00c04fc964ff ", true)]
    [DataRow("my-spa-client", false)]
    [DataRow("dcr_0123456789abcdef", false)]
    public void IsReservedClientId_RejectsEveryGuidFormat(string clientId, bool reserved)
        => Assert.AreEqual(reserved, MrWhoOidc.Auth.Utils.ClientSubject.IsReservedClientId(clientId));

    private static string CreateUnsignedToken(string issuer)
    {
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: "api",
            claims: [new Claim("sub", "user-123")],
            expires: DateTime.UtcNow.AddMinutes(5));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class RecordingTenantResolver : ITenantResolver
    {
        public string? LastResolvedPath { get; private set; }

        public Task<TenantContext?> ResolveTenantAsync(string path, CancellationToken cancellationToken = default)
        {
            LastResolvedPath = path;

            if (!string.Equals(path, "/t/default", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<TenantContext?>(null);
            }

            return Task.FromResult<TenantContext?>(new TenantContext
            {
                TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Slug = "default",
                Name = "Default Tenant",
                IssuerUri = "https://mrwho.onrender.com/t/default",
                IsMultiTenantMode = true
            });
        }
    }

    private sealed class CapturingTokenValidator(ITenantAccessor tenantAccessor, Claim[]? claims = null) : ITokenValidator
    {
        public TenantContext? ObservedTenant { get; private set; }

        public Task<(bool ok, ClaimsPrincipal? principal, string? error)> ValidateAsync(
            string token,
            string issuer,
            CancellationToken ct = default,
            IEnumerable<string>? validAudiences = null,
            bool skipAudienceValidation = false)
        {
            ObservedTenant = tenantAccessor.CurrentTenant;
            var identity = new ClaimsIdentity(claims ?? [new Claim("sub", "user-123")], ApiTokenAuthHandler.SchemeName);
            var principal = new ClaimsPrincipal(identity);
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