using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Cli.Configuration;

namespace MrWhoOidc.UnitTests.Cli;

[TestClass]
[DoNotParallelize]
public sealed class CliTokenProtectionTests
{
    private const string AccessToken = "eyJhbGciOiJSUzI1NiJ9.access-token-plaintext";
    private const string RefreshToken = "refresh-token-plaintext-value";

    private string? _originalConfigDir;
    private string _tempConfigDir = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _originalConfigDir = Environment.GetEnvironmentVariable("MRWHOOIDC_CONFIG_DIR");
        _tempConfigDir = Path.Combine(Path.GetTempPath(), $"mrwho-cli-dpapi-tests-{Guid.NewGuid():N}");
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
    public async Task Save_Protects_Token_Fields_And_Load_Restores_Them()
    {
        using var _ = CliTokenProtection.UseProtectorForTesting(new FakeProtector());

        await NewConfig().SaveAsync();

        var raw = await File.ReadAllTextAsync(CliConfig.GetConfigFilePath());
        StringAssert.DoesNotMatch(raw, new System.Text.RegularExpressions.Regex("access-token-plaintext|refresh-token-plaintext"));
        StringAssert.Contains(raw, "\"accessToken\": \"fake:");
        StringAssert.Contains(raw, "\"refreshToken\": \"fake:");

        var loaded = await CliConfig.LoadAsync();
        var profile = loaded.GetCurrentProfile()!;
        Assert.AreEqual(AccessToken, profile.AccessToken);
        Assert.AreEqual(RefreshToken, profile.RefreshToken);
        Assert.AreEqual("https://localhost:8443/t/default", profile.ServerUrl);
    }

    [TestMethod]
    public async Task Legacy_Plaintext_Tokens_Are_Read_And_Migrated_On_Next_Save()
    {
        Directory.CreateDirectory(_tempConfigDir);
        await File.WriteAllTextAsync(CliConfig.GetConfigFilePath(), $$"""
            {
              "currentProfile": "default",
              "profiles": {
                "default": {
                  "serverUrl": "https://localhost:8443/t/default",
                  "clientId": "mrwho-cli",
                  "accessToken": "{{AccessToken}}",
                  "refreshToken": "{{RefreshToken}}"
                }
              }
            }
            """);

        using var _ = CliTokenProtection.UseProtectorForTesting(new FakeProtector());

        var loaded = await CliConfig.LoadAsync();
        Assert.AreEqual(AccessToken, loaded.GetCurrentProfile()!.AccessToken);
        Assert.AreEqual(RefreshToken, loaded.GetCurrentProfile()!.RefreshToken);

        await loaded.SaveAsync();

        var raw = await File.ReadAllTextAsync(CliConfig.GetConfigFilePath());
        Assert.IsFalse(raw.Contains(AccessToken, StringComparison.Ordinal), "access token must be protected after re-save");
        Assert.IsFalse(raw.Contains(RefreshToken, StringComparison.Ordinal), "refresh token must be protected after re-save");
    }

    [TestMethod]
    public async Task Undecryptable_Token_Is_Treated_As_Logged_Out()
    {
        using (CliTokenProtection.UseProtectorForTesting(new FakeProtector()))
        {
            await NewConfig().SaveAsync();
        }

        using var _ = CliTokenProtection.UseProtectorForTesting(new FakeProtector(fail: true));
        var loaded = await CliConfig.LoadAsync();
        var profile = loaded.GetCurrentProfile()!;

        Assert.IsNull(profile.AccessToken);
        Assert.IsNull(profile.RefreshToken);
        Assert.IsFalse(profile.IsAuthenticated);
        Assert.AreEqual("https://localhost:8443/t/default", profile.ServerUrl);
    }

    [TestMethod]
    public async Task Dpapi_Round_Trip_On_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Linux/macOS: tokens stay plaintext in the 0600 config file (keychain integration is future work).
            await NewConfig().SaveAsync();
            var plain = await File.ReadAllTextAsync(CliConfig.GetConfigFilePath());
            StringAssert.Contains(plain, AccessToken);
            return;
        }

        await NewConfig().SaveAsync();
        var raw = await File.ReadAllTextAsync(CliConfig.GetConfigFilePath());
        StringAssert.Contains(raw, "\"accessToken\": \"dpapi:");
        Assert.IsFalse(raw.Contains(AccessToken, StringComparison.Ordinal));

        var loaded = await CliConfig.LoadAsync();
        Assert.AreEqual(AccessToken, loaded.GetCurrentProfile()!.AccessToken);
        Assert.AreEqual(RefreshToken, loaded.GetCurrentProfile()!.RefreshToken);
    }

    private static CliConfig NewConfig() => new()
    {
        CurrentProfile = "default",
        Profiles = new Dictionary<string, ProfileConfig>
        {
            ["default"] = new()
            {
                ServerUrl = "https://localhost:8443/t/default",
                ClientId = "mrwho-cli",
                AccessToken = AccessToken,
                RefreshToken = RefreshToken
            }
        }
    };

    private sealed class FakeProtector(bool fail = false) : ITokenProtector
    {
        public string Prefix => "fake:";

        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] ciphertext)
            => fail ? throw new CryptographicException("wrong user") : ciphertext.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}
