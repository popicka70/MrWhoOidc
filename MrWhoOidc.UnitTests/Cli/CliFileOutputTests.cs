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

    [TestMethod]
    public async Task WriteTextAsync_CreatesOwnerOnlyFileAndDirectory_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes only.");
        }

        var path = await CliFileOutput.WriteTextAsync("secret", "secret.txt");

        Assert.AreEqual(OwnerOnlyFile.OwnerReadWrite, File.GetUnixFileMode(path));
        Assert.AreEqual(OwnerOnlyFile.OwnerDirectory, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
    }

    [TestMethod]
    public async Task WriteTextAsync_Overwrite_TightensPreviouslyWorldReadableFile_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes only.");
        }

        var path = CliFileOutput.ResolveOutputPath("loose.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await CliFileOutput.WriteTextAsync("new", "loose.txt", overwrite: true);

        Assert.AreEqual("new", await File.ReadAllTextAsync(path));
        Assert.AreEqual(OwnerOnlyFile.OwnerReadWrite, File.GetUnixFileMode(path));
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length, "temp file must not be left behind");
    }

    [TestMethod]
    public async Task CliConfig_SaveAsync_WritesOwnerOnlyConfig_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes only.");
        }

        var config = new CliConfig();
        config.SetProfile("default", new ProfileConfig { ServerUrl = "https://localhost:8443", AccessToken = "tok" });
        await config.SaveAsync();
        await config.SaveAsync(); // second save replaces the existing file

        var path = CliConfig.GetConfigFilePath();
        Assert.AreEqual(OwnerOnlyFile.OwnerReadWrite, File.GetUnixFileMode(path));
        Assert.AreEqual(OwnerOnlyFile.OwnerDirectory, File.GetUnixFileMode(CliConfig.GetConfigDirectory()));
        Assert.AreEqual("tok", (await CliConfig.LoadAsync()).GetCurrentProfile()?.AccessToken);
    }
}
