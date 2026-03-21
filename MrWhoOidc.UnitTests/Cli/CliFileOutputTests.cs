using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;

namespace MrWhoOidc.UnitTests.Cli;

[TestClass]
[DoNotParallelize]
public sealed class CliFileOutputTests
{
    private string? _originalConfigDir;
    private string _tempConfigDir = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _originalConfigDir = Environment.GetEnvironmentVariable("MRWHOOIDC_CONFIG_DIR");
        _tempConfigDir = Path.Combine(Path.GetTempPath(), $"mrwho-cli-file-tests-{Guid.NewGuid():N}");
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
    public async Task WriteTextAsync_UsesDefaultExportDirectory()
    {
        var path = await CliFileOutput.WriteTextAsync("{\"ok\":true}", "sample.json");

        Assert.IsTrue(File.Exists(path));
        StringAssert.StartsWith(path, Path.Combine(_tempConfigDir, "exports"));
    }

    [TestMethod]
    public async Task WriteTextAsync_RejectsExistingFileWithoutOverwrite()
    {
        var path = await CliFileOutput.WriteTextAsync("first", "sample.json");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CliFileOutput.WriteTextAsync("second", "sample.json"));
        Assert.AreEqual("first", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task WriteTextAsync_AllowsOverwriteWhenRequested()
    {
        var path = await CliFileOutput.WriteTextAsync("first", "sample.json");
        var overwritten = await CliFileOutput.WriteTextAsync("second", "sample.json", overwrite: true);

        Assert.AreEqual(path, overwritten);
        Assert.AreEqual("second", await File.ReadAllTextAsync(path));
    }
}