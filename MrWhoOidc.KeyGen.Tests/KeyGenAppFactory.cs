using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace MrWhoOidc.KeyGen.Tests;

/// <summary>
/// Hosts the real KeyGen Program with a throw-away SQLite database and licensing key.
/// </summary>
internal sealed partial class KeyGenAppFactory : WebApplicationFactory<Program>
{
    public const string TestAuthority = "https://idp.test/t/default";
    public const string TestAuthorizationEndpoint = "https://idp.test/t/default/authorize";

    private readonly string _environment;
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly bool _useTestUserScheme;
    private readonly string _directory;

    private KeyGenAppFactory(string environment, IReadOnlyDictionary<string, string?> settings, bool useTestUserScheme)
    {
        _environment = environment;
        _settings = settings;
        _useTestUserScheme = useTestUserScheme;
        _directory = Path.Combine(Path.GetTempPath(), "keygen-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(KeyPath, ecdsa.ExportECPrivateKeyPem());
    }

    private string KeyPath => Path.Combine(_directory, "licensing-key.pem");

    public string DatabasePath => Path.Combine(_directory, "keygen.db");

    /// <summary>Production with OIDC configured (the IdP metadata is stubbed, no network).</summary>
    public static KeyGenAppFactory ProductionWithOidc(bool useTestUserScheme = false) =>
        new("Production", new Dictionary<string, string?>
        {
            ["KeyGen:Auth:Authority"] = TestAuthority,
            ["KeyGen:Auth:ClientId"] = "keygen"
        }, useTestUserScheme);

    public static KeyGenAppFactory Create(string environment, IReadOnlyDictionary<string, string?> authSettings) =>
        new(environment, authSettings, useTestUserScheme: false);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("ConnectionStrings:KeyGenDb", $"Data Source={DatabasePath}");
        builder.UseSetting("KeyGen:LicensingPrivateKeyPath", KeyPath);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.ClearProviders());

        builder.ConfigureTestServices(services =>
        {
            // Static IdP metadata so a challenge builds the authorize redirect without network I/O.
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = TestAuthority,
                    AuthorizationEndpoint = TestAuthorizationEndpoint,
                    TokenEndpoint = TestAuthority + "/token"
                };
            });

            if (_useTestUserScheme)
            {
                // Stands in for the cookie a completed OIDC sign-in would produce.
                services.AddAuthentication(options =>
                    {
                        options.DefaultScheme = TestUserHandler.SchemeName;
                        options.DefaultAuthenticateScheme = TestUserHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestUserHandler>(TestUserHandler.SchemeName, _ => { });
            }
        });
    }

    public HttpClient CreateNonRedirectingClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = AntiforgeryTokenRegex().Match(html);
        Assert.IsTrue(match.Success, $"No antiforgery token found on {path}");
        return match.Groups[1].Value;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();
}

/// <summary>
/// Authenticates from test headers: <c>X-Test-Sub</c>, <c>X-Test-Name</c>, <c>X-Test-Email</c>
/// and a comma-separated <c>X-Test-Roles</c>. No <c>X-Test-Sub</c> means anonymous.
/// </summary>
internal sealed class TestUserHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestUser";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var sub = Request.Headers["X-Test-Sub"].ToString();
        if (string.IsNullOrEmpty(sub))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("sub", sub) };
        if (Request.Headers.TryGetValue("X-Test-Name", out var name))
        {
            claims.Add(new Claim("name", name.ToString()));
        }

        if (Request.Headers.TryGetValue("X-Test-Email", out var email))
        {
            claims.Add(new Claim("email", email.ToString()));
        }

        foreach (var role in Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            claims.Add(new Claim("roles", role));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, "name", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
