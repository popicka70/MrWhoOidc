using Microsoft.VisualStudio.TestTools.UnitTesting;
using CliProgram = MrWhoOidc.Cli.Program;
using MrWhoOidc.Cli.Services;

namespace MrWhoOidc.UnitTests.Cli;

[TestClass]
[DoNotParallelize]
public sealed class CliTlsValidationTests
{
    private bool _originalFlag;
    private string? _originalEnv;

    [TestInitialize]
    public void TestInitialize()
    {
        _originalFlag = CliServerConnection.AllowInsecureLoopbackTls;
        _originalEnv = Environment.GetEnvironmentVariable(CliServerConnection.InsecureLoopbackTlsEnvironmentVariable);
        CliServerConnection.AllowInsecureLoopbackTls = false;
        Environment.SetEnvironmentVariable(CliServerConnection.InsecureLoopbackTlsEnvironmentVariable, null);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        CliServerConnection.AllowInsecureLoopbackTls = _originalFlag;
        Environment.SetEnvironmentVariable(CliServerConnection.InsecureLoopbackTlsEnvironmentVariable, _originalEnv);
    }

    [TestMethod]
    [DataRow("https://localhost:8443")]
    [DataRow("https://127.0.0.1:8443/t/default")]
    [DataRow("https://[::1]:8443")]
    public void Loopback_ValidatesCertificate_ByDefault(string server)
    {
        Assert.IsFalse(CliServerConnection.ShouldSkipTlsValidation(server));
        using var handler = CliServerConnection.CreateHttpHandler(server);
        Assert.IsNull(handler.ServerCertificateCustomValidationCallback);
    }

    [TestMethod]
    [DataRow("https://localhost:8443")]
    [DataRow("https://127.0.0.1:8443/t/default")]
    [DataRow("https://[::1]:8443")]
    public void Loopback_SkipsValidation_OnlyWithInsecureFlag(string server)
    {
        CliServerConnection.AllowInsecureLoopbackTls = true;

        Assert.IsTrue(CliServerConnection.ShouldSkipTlsValidation(server));
        using var handler = CliServerConnection.CreateHttpHandler(server);
        Assert.IsNotNull(handler.ServerCertificateCustomValidationCallback);
    }

    [TestMethod]
    public void Loopback_SkipsValidation_WithEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(CliServerConnection.InsecureLoopbackTlsEnvironmentVariable, "1");

        Assert.IsTrue(CliServerConnection.ShouldSkipTlsValidation("https://localhost:8443"));
    }

    [TestMethod]
    [DataRow("https://auth.example.com")]
    [DataRow("https://localhost.example.com")]
    [DataRow("https://10.0.0.5:8443")]
    public void NonLoopback_AlwaysValidates_EvenWithInsecureFlag(string server)
    {
        CliServerConnection.AllowInsecureLoopbackTls = true;
        Environment.SetEnvironmentVariable(CliServerConnection.InsecureLoopbackTlsEnvironmentVariable, "true");

        Assert.IsFalse(CliServerConnection.ShouldSkipTlsValidation(server));
        using var handler = CliServerConnection.CreateHttpHandler(server);
        Assert.IsNull(handler.ServerCertificateCustomValidationCallback);
    }

    [TestMethod]
    public void RootCommand_ParsesInsecureFlag_OnSubcommands()
    {
        var root = CliProgram.BuildRootCommand();
        var option = root.Options.OfType<System.CommandLine.Option<bool>>().Single(o => o.Name == CliProgram.InsecureFlag);

        var parsed = root.Parse(["discovery", "--server", "https://localhost:8443", "--insecure"]);

        Assert.AreEqual(0, parsed.Errors.Count, string.Join("; ", parsed.Errors.Select(e => e.Message)));
        Assert.IsTrue(parsed.GetValue(option));
    }

    [TestMethod]
    public void McpArgs_DefaultToReadOnlyAndSecureTls()
    {
        var options = CliProgram.ParseMcpArgs([]);

        Assert.IsFalse(options.AllowWrites);
        Assert.IsFalse(options.Insecure);
    }

    [TestMethod]
    public void McpArgs_ParseFlags_AndRejectUnknown()
    {
        var options = CliProgram.ParseMcpArgs(["--allow-writes", "--insecure"]);
        Assert.IsTrue(options.AllowWrites);
        Assert.IsTrue(options.Insecure);

        Assert.ThrowsExactly<ArgumentException>(() => CliProgram.ParseMcpArgs(["--allow-write"]));
    }
}
