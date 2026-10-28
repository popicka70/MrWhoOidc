using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;
using CliProgram = MrWhoOidc.Cli.Program;

namespace MrWhoOidc.UnitTests.Cli;

/// <summary>
/// CLI hardening from the 2026-10-26 review:
/// M-Path (server-controlled Content-Disposition file name), M-CliCfg (a corrupt config silently wiped every
/// profile) and M-MCP-stdout (non-protocol text on the MCP JSON-RPC stream).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CliSafetyTests
{
    private string? _originalConfigDir;
    private string _tempConfigDir = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _originalConfigDir = Environment.GetEnvironmentVariable("MRWHOOIDC_CONFIG_DIR");
        _tempConfigDir = Path.Combine(Path.GetTempPath(), $"mrwho-cli-safety-{Guid.NewGuid():N}");
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

    // ── M-Path ────────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("../../escape.json")]
    [DataRow("..\\..\\escape.json")]
    [DataRow("/etc/passwd")]
    [DataRow("....//....//escape.json")]
    public void ResolveOutputPath_StaysInsideTheExportsDirectory(string suggested)
    {
        var exportsDir = Path.GetFullPath(CliFileOutput.GetDefaultExportsDirectory());
        var resolved = Path.GetFullPath(CliFileOutput.ResolveOutputPath(suggested));

        Assert.IsTrue(resolved.StartsWith(exportsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"'{suggested}' resolved to '{resolved}', which is outside '{exportsDir}'");
    }

    [TestMethod]
    public async Task WriteTextAsync_IgnoresATraversalFileName()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"mrwho-cli-escape-{Guid.NewGuid():N}.json");

        var path = await CliFileOutput.WriteTextAsync("{}", "../../" + outside);

        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(outside), "the server-controlled file name must not write outside the exports directory");
        StringAssert.StartsWith(path, Path.GetFullPath(CliFileOutput.GetDefaultExportsDirectory()));
    }

    [TestMethod]
    public void SanitizeFileName_KeepsOnlySafeCharacters()
    {
        Assert.AreEqual("export.json", CliFileOutput.SanitizeFileName("export.json"));
        Assert.AreEqual("my-file_1.json", CliFileOutput.SanitizeFileName("my-file_1.json"));
        Assert.AreEqual("passwd", CliFileOutput.SanitizeFileName("/etc/passwd"));
        Assert.AreEqual("evil", CliFileOutput.SanitizeFileName("../../evil"));
        Assert.AreEqual("export.json", CliFileOutput.SanitizeFileName(".."));
        Assert.AreEqual("export.json", CliFileOutput.SanitizeFileName(""));
    }

    // ── M-CliCfg ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task LoadAsync_FailsLoudlyOnACorruptConfig_AndQuarantinesIt()
    {
        var configPath = CliConfig.GetConfigFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, "{ this is not json");

        var threw = false;
        try
        {
            await CliConfig.LoadAsync();
        }
        catch (InvalidOperationException ex)
        {
            threw = true;
            StringAssert.Contains(ex.Message, "corrupt");
        }

        Assert.IsTrue(threw, "a corrupt config must not be reported as an empty configuration");
        Assert.IsFalse(File.Exists(configPath), "the corrupt config must be quarantined, not left in place");
    }

    [TestMethod]
    public async Task LoadAsync_CorruptConfig_DoesNotWipeTheExistingProfiles()
    {
        var config = new CliConfig();
        config.SetProfile("default", new ProfileConfig { ServerUrl = "https://localhost:8443", AccessToken = "tok" });
        await config.SaveAsync();

        var configPath = CliConfig.GetConfigFilePath();
        var original = await File.ReadAllTextAsync(configPath);
        // Corrupt the file without throwing the stored profiles away.
        await File.WriteAllTextAsync(configPath, original + " GARBAGE");

        try
        {
            await CliConfig.LoadAsync();
            Assert.Fail("a corrupt config must fail loudly");
        }
        catch (InvalidOperationException)
        {
            // expected
        }

        var quarantined = Directory.GetFiles(Path.GetDirectoryName(configPath)!, "config.json.corrupt-*");
        Assert.AreEqual(1, quarantined.Length, "the corrupt config must be quarantined for inspection");

        // The operator's data is still there: it was moved aside, not truncated.
        var content = await File.ReadAllTextAsync(quarantined[0]);
        StringAssert.Contains(content, "tok");
    }

    [TestMethod]
    public async Task LoadAsync_ReturnsAnEmptyConfigWhenNoFileExists()
    {
        var config = await CliConfig.LoadAsync();
        Assert.AreEqual(0, config.Profiles.Count);
    }

    // ── M-MCP-stdout ──────────────────────────────────────────────────────────

    [TestMethod]
    public void McpBanner_IsWrittenToTheGivenStreamOnly()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        CliProgram.WriteMcpBanner(allowWrites: true, stderr);

        Assert.AreEqual(string.Empty, stdout.ToString());
        StringAssert.Contains(stderr.ToString(), "Starting MCP server");
        StringAssert.Contains(stderr.ToString(), "--allow-writes");
    }

    [TestMethod]
    public async Task McpMode_FatalErrorGoesToStderr_NotToStdout()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            // An unknown MCP argument is rejected before the server starts (no stdin is consumed).
            var exitCode = await CliProgram.Main(["mcp", "--not-a-flag"]);

            Assert.AreEqual(1, exitCode);
            StringAssert.Contains(stderr.ToString(), "Fatal error");
            Assert.AreEqual(string.Empty, stdout.ToString(),
                "stdout carries the JSON-RPC stream in MCP mode and must stay free of diagnostics");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
