using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Cli.Mcp;
using MrWhoOidc.Cli.Services;

namespace MrWhoOidc.UnitTests.Cli;

[TestClass]
[DoNotParallelize]
public sealed class McpToolRegistryTests
{
    private static readonly string[] WriteTools = ["client_create", "scope_create", "user_create", "invitation_create", "invitation_revoke"];

    private string? _originalConfigDir;
    private string _tempConfigDir = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _originalConfigDir = Environment.GetEnvironmentVariable("MRWHOOIDC_CONFIG_DIR");
        _tempConfigDir = Path.Combine(Path.GetTempPath(), $"mrwho-mcp-tests-{Guid.NewGuid():N}");
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
    public void DefaultRegistry_DoesNotListWriteTools()
    {
        var names = new McpToolRegistry().GetAllTools().Select(t => t.Name).ToHashSet();

        foreach (var tool in WriteTools)
        {
            Assert.IsFalse(names.Contains(tool), $"{tool} must not be exposed without --allow-writes");
        }

        Assert.IsTrue(names.Contains("client_list"));
        Assert.IsTrue(names.Contains("setup_guide"));
    }

    [TestMethod]
    public async Task DefaultRegistry_RefusesToExecuteWriteTools()
    {
        var registry = new McpToolRegistry();

        foreach (var tool in WriteTools)
        {
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => registry.ExecuteToolAsync(tool, new Dictionary<string, JsonElement>(), CancellationToken.None));
            StringAssert.Contains(ex.Message, "--allow-writes");
        }
    }

    [TestMethod]
    public void AllowWritesRegistry_ListsWriteTools()
    {
        var names = new McpToolRegistry(allowWrites: true).GetAllTools().Select(t => t.Name).ToHashSet();

        foreach (var tool in WriteTools)
        {
            Assert.IsTrue(names.Contains(tool), $"{tool} should be exposed with --allow-writes");
        }
    }

    [TestMethod]
    public void InvitationCreate_SchemaDoesNotOfferTenantAdmin_AndUserCreateHasNoPassword()
    {
        var tools = new McpToolRegistry(allowWrites: true).GetAllTools().ToDictionary(t => t.Name);

        var invitationProps = tools["invitation_create"].InputSchema.GetProperty("properties");
        Assert.IsFalse(invitationProps.TryGetProperty("isTenantAdmin", out _));

        var userProps = tools["user_create"].InputSchema.GetProperty("properties");
        Assert.IsFalse(userProps.TryGetProperty("password", out _));
    }

    [TestMethod]
    public async Task InvitationCreate_RejectsTenantAdmin_BeforeAnyNetworkCall()
    {
        var registry = new McpToolRegistry(allowWrites: true);
        var args = new Dictionary<string, JsonElement>
        {
            ["email"] = JsonSerializer.SerializeToElement("eve@example.com"),
            ["isTenantAdmin"] = JsonSerializer.SerializeToElement(true)
        };

        var result = await registry.ExecuteToolAsync("invitation_create", args, CancellationToken.None);

        var text = JsonSerializer.Serialize(result);
        StringAssert.Contains(text, "ERROR");
        StringAssert.Contains(text, "--tenant-admin");
    }

    [TestMethod]
    public async Task UserCreate_RejectsSuppliedPassword()
    {
        var registry = new McpToolRegistry(allowWrites: true);
        var args = new Dictionary<string, JsonElement>
        {
            ["username"] = JsonSerializer.SerializeToElement("bob"),
            ["password"] = JsonSerializer.SerializeToElement("hunter2")
        };

        var result = await registry.ExecuteToolAsync("user_create", args, CancellationToken.None);

        StringAssert.Contains(JsonSerializer.Serialize(result), "ERROR");
    }

    [TestMethod]
    public async Task RedactSecretsToFile_RemovesSecretsFromResponse_AndWritesOwnerOnlyFile()
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            id = "c0ffee",
            clientId = "my-app",
            initialSecret = "s3cr3t-value",
            nested = new { token = "invite-token" }
        });

        var (redacted, secretFile) = await McpToolRegistry.RedactSecretsToFileAsync(
            response, ["initialSecret", "token"], "client-../../evil-secret", CancellationToken.None);

        var redactedJson = redacted!.ToJsonString();
        Assert.IsFalse(redactedJson.Contains("s3cr3t-value"));
        Assert.IsFalse(redactedJson.Contains("invite-token"));
        StringAssert.Contains(redactedJson, "my-app");

        Assert.IsNotNull(secretFile);
        Assert.AreEqual(Path.GetFullPath(CliFileOutput.GetDefaultExportsDirectory()), Path.GetDirectoryName(Path.GetFullPath(secretFile)));
        var fileContent = await File.ReadAllTextAsync(secretFile);
        StringAssert.Contains(fileContent, "s3cr3t-value");
        StringAssert.Contains(fileContent, "invite-token");

        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(OwnerOnlyFile.OwnerReadWrite, File.GetUnixFileMode(secretFile));
        }
    }

    [TestMethod]
    public async Task RedactSecretsToFile_WithoutSecrets_WritesNothing()
    {
        var response = JsonSerializer.SerializeToElement(new { id = "c0ffee", initialSecret = (string?)null });

        var (redacted, secretFile) = await McpToolRegistry.RedactSecretsToFileAsync(
            response, ["initialSecret"], "client-x-secret", CancellationToken.None);

        Assert.IsNull(secretFile);
        Assert.IsNotNull(redacted);
        Assert.IsFalse(Directory.Exists(CliFileOutput.GetDefaultExportsDirectory()));
    }
}
