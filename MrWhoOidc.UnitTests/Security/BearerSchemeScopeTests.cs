using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth.Security.ApiBearer;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// V2 of the third 2026-10-04 review: the default "auto" scheme forwarded every Authorization: Bearer request to the
/// api-bearer handler, so on /authorize, consent and the account pages any leaked access token acted as a browser
/// login that skipped password, MFA and acr_values.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BearerSchemeScopeTests
{
    private static SharedWebAppFixture _fixture = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new SharedWebAppFixture();

    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    private static async Task<AuthenticateResult> AuthenticateDefaultAsync(string path)
    {
        using var scope = _fixture.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = path;
        // Not a valid JWT: the bearer handler fails it, the cookie handler never looks at it.
        context.Request.Headers.Authorization = "Bearer not-a-jwt";
        return await context.AuthenticateAsync();
    }

    [TestMethod]
    [DataRow("/authorize")]
    [DataRow("/t/default/authorize")]
    [DataRow("/consent")]
    [DataRow("/t/default/Password")]
    [DataRow("/t/default/Mfa")]
    [DataRow("/api/webauthn/registration/options")]
    [DataRow("/t/default/admin/apiX")]
    public async Task BrowserPaths_IgnoreBearerTokens(string path)
    {
        var result = await AuthenticateDefaultAsync(path);

        Assert.IsTrue(result.None, $"{path}: a bearer token must not be evaluated outside the admin APIs (failure: {result.Failure?.Message})");
    }

    [TestMethod]
    [DataRow("/admin/api/clients")]
    [DataRow("/t/default/admin/api/users")]
    [DataRow("/T/Default/Admin/Api/users")]
    [DataRow("/platform-admin/api/tenants")]
    [DataRow("/admin/api/platform/tenants/default/export")]
    public async Task AdminApiPaths_StillAcceptBearerTokens(string path)
    {
        var result = await AuthenticateDefaultAsync(path);

        Assert.IsNotNull(result.Failure, $"{path}: the bearer handler should have evaluated (and rejected) the token");
    }

    [TestMethod]
    [DataRow("/t//admin/api/users", false)]
    [DataRow("/t/a/b/admin/api", false)]
    [DataRow("/tx/admin/api", false)]
    [DataRow("/t/acme/admin/api", true)]
    public void IsBearerApiPath_MatchesOnlyTheTenantAdminApiShape(string path, bool expected)
        => Assert.AreEqual(expected, ApiTokenAuthHandler.IsBearerApiPath(path));
}
