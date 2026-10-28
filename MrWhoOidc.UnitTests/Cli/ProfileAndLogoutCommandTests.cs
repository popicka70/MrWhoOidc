using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Cli.Commands;
using MrWhoOidc.Cli.Configuration;

namespace MrWhoOidc.UnitTests.Cli;

[TestClass]
[DoNotParallelize]
public sealed class ProfileAndLogoutCommandTests
{
    private string? _originalConfigDir;
    private string _tempConfigDir = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _originalConfigDir = Environment.GetEnvironmentVariable("MRWHOOIDC_CONFIG_DIR");
        _tempConfigDir = Path.Combine(Path.GetTempPath(), $"mrwho-cli-tests-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("MRWHOOIDC_CONFIG_DIR", _tempConfigDir);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        Environment.SetEnvironmentVariable("MRWHOOIDC_CONFIG_DIR", _originalConfigDir);

        if (Directory.Exists(_tempConfigDir))
        {
            Directory.Delete(_tempConfigDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProfileSwitch_UpdatesCurrentProfile()
    {
        var config = new CliConfig
        {
            CurrentProfile = "alpha",
            Profiles = new Dictionary<string, ProfileConfig>
            {
                ["alpha"] = new() { ServerUrl = "https://localhost:8443/t/default" },
                ["beta"] = new() { ServerUrl = "https://localhost:8443/t/other" }
            }
        };
        await config.SaveAsync();

        var exitCode = await new ProfileCommand().Parse(["switch", "beta"]).InvokeAsync();
        var saved = await CliConfig.LoadAsync();

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("beta", saved.CurrentProfile);
    }

    [TestMethod]
    public async Task ProfileShow_WithoutName_UsesCurrentProfile()
    {
        var config = new CliConfig
        {
            CurrentProfile = "default",
            Profiles = new Dictionary<string, ProfileConfig>
            {
                ["default"] = new() { ServerUrl = "https://localhost:8443/t/default" }
            }
        };
        await config.SaveAsync();

        var exitCode = await new ProfileCommand().Parse(["show", "--format", "json"]).InvokeAsync();

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task ProfileRemove_DeletesProfileAndFallsBackToRemainingProfile()
    {
        var config = new CliConfig
        {
            CurrentProfile = "alpha",
            Profiles = new Dictionary<string, ProfileConfig>
            {
                ["alpha"] = new() { ServerUrl = "https://localhost:8443/t/default" },
                ["beta"] = new() { ServerUrl = "https://localhost:8443/t/other" }
            }
        };
        await config.SaveAsync();

        var exitCode = await new ProfileCommand().Parse(["remove", "alpha"]).InvokeAsync();
        var saved = await CliConfig.LoadAsync();

        Assert.AreEqual(0, exitCode);
        Assert.IsFalse(saved.Profiles.ContainsKey("alpha"));
        Assert.AreEqual("beta", saved.CurrentProfile);
    }

    [TestMethod]
    public async Task Logout_ClearsStoredTokensForCurrentProfile()
    {
        var config = new CliConfig
        {
            CurrentProfile = "default",
            Profiles = new Dictionary<string, ProfileConfig>
            {
                ["default"] = new()
                {
                    ServerUrl = "https://localhost:8443/t/default",
                    ClientId = "mrwho-cli-default",
                    AccessToken = "access-token",
                    RefreshToken = "refresh-token",
                    TokenExpiry = DateTimeOffset.UtcNow.AddMinutes(15),
                    TokenIntrospectedAt = DateTimeOffset.UtcNow
                }
            }
        };
        await config.SaveAsync();

        var exitCode = await new LogoutCommand().Parse(Array.Empty<string>()).InvokeAsync();
        var saved = await CliConfig.LoadAsync();
        var profile = saved.GetCurrentProfile();

        Assert.AreEqual(0, exitCode);
        Assert.IsNotNull(profile);
        Assert.IsNull(profile.AccessToken);
        Assert.IsNull(profile.RefreshToken);
        Assert.IsNull(profile.TokenExpiry);
        Assert.IsNull(profile.TokenIntrospectedAt);
    }

    [TestMethod]
    public async Task Logout_RevokesTheTokensOnTheServer()
    {
        // Clearing the tokens locally leaves a live refresh token behind: the sign-out must revoke it.
        using var server = new LoopbackServer();
        var config = new CliConfig
        {
            CurrentProfile = "default",
            Profiles = new Dictionary<string, ProfileConfig>
            {
                ["default"] = new()
                {
                    ServerUrl = server.Url + "/t/default",
                    ClientId = "mrwho-cli-default",
                    AccessToken = "access-token",
                    RefreshToken = "refresh-token",
                    TokenExpiry = DateTimeOffset.UtcNow.AddMinutes(15),
                    TokenIntrospectedAt = DateTimeOffset.UtcNow
                }
            }
        };
        await config.SaveAsync();

        var exitCode = await new LogoutCommand().Parse(Array.Empty<string>()).InvokeAsync();
        var saved = await CliConfig.LoadAsync();

        Assert.AreEqual(0, exitCode);
        Assert.IsNotNull(saved.GetCurrentProfile());
        Assert.IsNull(saved.GetCurrentProfile().AccessToken);
        Assert.IsNull(saved.GetCurrentProfile().RefreshToken);
        Assert.IsTrue(server.RevocationRequests.Any(r => r.Contains("token=refresh-token", StringComparison.Ordinal)),
            "the refresh token must be revoked on the server.");
        Assert.IsTrue(server.RevocationRequests.Any(r => r.Contains("client_id=mrwho-cli-default", StringComparison.Ordinal)));
    }

    /// <summary>A loopback HTTP server that answers the discovery document and records the revocations.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public LoopbackServer()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public List<string> RevocationRequests { get; } = new();

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
                {
                    var document = $"{{\"issuer\":\"{Url}/t/default\",\"authorization_endpoint\":\"{Url}/authorize\",\"token_endpoint\":\"{Url}/token\",\"jwks_uri\":\"{Url}/jwks\",\"revocation_endpoint\":\"{Url}/revoke\"}}";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(document);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                else if (path.EndsWith("/revoke", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                    RevocationRequests.Add(await reader.ReadToEndAsync());
                    context.Response.StatusCode = 200;
                }
                else
                {
                    context.Response.StatusCode = 404;
                }

                context.Response.Close();
            }
        }
    }
}
