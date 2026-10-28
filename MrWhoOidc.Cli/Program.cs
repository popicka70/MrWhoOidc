using System.CommandLine;
using MrWhoOidc.Cli.Commands;
using MrWhoOidc.Cli.Mcp;
using MrWhoOidc.Cli.Services;
using Spectre.Console;

namespace MrWhoOidc.Cli;

/// <summary>
/// MrWhoOidc CLI - Manage OIDC server via command line or MCP protocol.
/// Supports dual-mode operation: standalone CLI and MCP server for LLM integration.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // M-MCP-stdout: in MCP mode stdout carries the JSON-RPC stream, so every diagnostic (including the
        // top-level failure handler) must go to stderr instead.
        var isMcpMode = args.Length > 0 && args[0] == "mcp";

        try
        {
            // Detect MCP mode (stdio server for LLM integration)
            if (isMcpMode)
            {
                return await RunMcpServerAsync(args[1..]);
            }

            // Standard CLI mode
            return await RunCliAsync(args);
        }
        catch (Exception ex)
        {
            if (isMcpMode)
            {
                Console.Error.WriteLine($"Fatal error: {ex.Message}");
                if (args.Contains("--verbose"))
                {
                    Console.Error.WriteLine(ex.ToString());
                }

                return 1;
            }

            AnsiConsole.MarkupLine($"[red]Fatal error:[/] {ex.Message}");
            if (args.Contains("--verbose"))
            {
                AnsiConsole.WriteException(ex);
            }
            return 1;
        }
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        var rootCommand = BuildRootCommand();
        try
        {
            var parseResult = rootCommand.Parse(args);
            // Propagate --dry-run to the API client before invoking subcommands
            var dryRunOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--dry-run");
            if (dryRunOpt is not null)
                CliAdminApiClient.IsDryRun = parseResult.GetValue(dryRunOpt);
            // Propagate --insecure (skip TLS validation, loopback servers only)
            var insecureOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == InsecureFlag);
            if (insecureOpt is not null && parseResult.GetValue(insecureOpt))
                CliServerConnection.AllowInsecureLoopbackTls = true;
            return await parseResult.InvokeAsync();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            if (args.Contains("--verbose"))
            {
                AnsiConsole.WriteException(ex);
            }
            return 1;
        }
    }

    internal sealed record McpOptions(bool AllowWrites, bool Insecure);

    /// <summary>
    /// Parses <c>mrwho-cli mcp [--allow-writes] [--insecure]</c>. Unknown arguments are rejected so a
    /// typo cannot silently start the server in an unexpected mode.
    /// </summary>
    internal static McpOptions ParseMcpArgs(IReadOnlyList<string> args)
    {
        var allowWrites = false;
        var insecure = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case McpToolRegistry.AllowWritesFlag:
                    allowWrites = true;
                    break;
                case InsecureFlag:
                    insecure = true;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown mcp argument '{arg}'. Usage: mrwho-cli mcp [{McpToolRegistry.AllowWritesFlag}] [{InsecureFlag}]");
            }
        }

        return new McpOptions(allowWrites, insecure);
    }

    private static async Task<int> RunMcpServerAsync(string[] args)
    {
        var options = ParseMcpArgs(args);
        CliServerConnection.AllowInsecureLoopbackTls |= options.Insecure;

        // M-MCP-stdout: stdout is the JSON-RPC channel. The banner goes to stderr so a strict MCP client
        // never sees non-protocol text in the stream.
        WriteMcpBanner(options.AllowWrites, Console.Error);

        var server = new McpServer(options.AllowWrites);
        await server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());

        return 0;
    }

    /// <summary>
    /// Writes the MCP startup banner. It is written to the given stream (stderr in production) because stdout
    /// carries the JSON-RPC messages and any extra text breaks strict MCP clients.
    /// </summary>
    internal static void WriteMcpBanner(bool allowWrites, TextWriter stderr)
    {
        stderr.WriteLine("Starting MCP server (stdio mode)...");
        stderr.WriteLine("Listening for JSON-RPC requests on stdin");
        stderr.WriteLine(allowWrites
            ? "MCP write tools ENABLED (--allow-writes): the connected LLM can create clients, users, scopes and invitations."
            : $"MCP server is read-only. Start with {McpToolRegistry.AllowWritesFlag} to expose write tools.");
    }

    internal const string InsecureFlag = "--insecure";

    internal static Option<bool> CreateInsecureOption() => new(InsecureFlag)
    {
        Description = "Skip TLS certificate validation for loopback servers (localhost/127.0.0.1/::1) only. " +
                      $"Prefer trusting the dev certificate: dotnet dev-certs https --trust. Env: {CliServerConnection.InsecureLoopbackTlsEnvironmentVariable}=1",
        Recursive = true
    };

    internal static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("mrwho-cli - Configure and operate your MrWhoOidc IdP");

        // Global options
        var profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Configuration profile to use (recommended: one per environment)"
        };

        var serverOption = new Option<string?>("--server", "-s")
        {
            Description = "Server URL override for this invocation (takes precedence over profile)"
        };

        var formatOption = new Option<OutputFormat>("--format", "-f")
        {
            Description = "Output format for results (table, json, yaml)",
            DefaultValueFactory = _ => OutputFormat.Table
        };

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose output"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Preview write operations without applying changes (where supported)"
        };

        rootCommand.Options.Add(profileOption);
        rootCommand.Options.Add(serverOption);
        rootCommand.Options.Add(formatOption);
        rootCommand.Options.Add(verboseOption);
        rootCommand.Options.Add(dryRunOption);
        rootCommand.Options.Add(CreateInsecureOption());

        // Add command groups (will be implemented in phases)
        rootCommand.Subcommands.Add(new LoginCommand());
        rootCommand.Subcommands.Add(new LogoutCommand());
        rootCommand.Subcommands.Add(new ProfileCommand());
        rootCommand.Subcommands.Add(new DiscoveryCommand());
        rootCommand.Subcommands.Add(new ExportCommand());
        rootCommand.Subcommands.Add(new ImportCommand());
        rootCommand.Subcommands.Add(new TenantCommand());
        rootCommand.Subcommands.Add(new RealmCommand());
        rootCommand.Subcommands.Add(new ClientCommand());
        rootCommand.Subcommands.Add(new ScopeCommand());
        rootCommand.Subcommands.Add(new UserCommand());
        rootCommand.Subcommands.Add(new InvitationCommand());
        rootCommand.Subcommands.Add(new RegistrationCommand());
        rootCommand.Subcommands.Add(new ProviderCommand());
        rootCommand.Subcommands.Add(new RoleCommand());
        rootCommand.Subcommands.Add(new HealthCommand());
        rootCommand.Subcommands.Add(new WhoAmICommand());
        rootCommand.Subcommands.Add(new AuditCommand());
        rootCommand.Subcommands.Add(new BclCommand());
        rootCommand.Subcommands.Add(new RateLimitsCommand());
        rootCommand.Subcommands.Add(new LicenseCommand());
        rootCommand.Subcommands.Add(new SetupCommand());

        return rootCommand;
    }
}

public enum OutputFormat
{
    Table,
    Json,
    Yaml
}
