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
}