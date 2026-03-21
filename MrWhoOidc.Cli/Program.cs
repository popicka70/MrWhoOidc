using System.CommandLine;
using MrWhoOidc.Cli.Commands;
using MrWhoOidc.Cli.Mcp;
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
        try
        {
            // Detect MCP mode (stdio server for LLM integration)
            if (args.Length > 0 && args[0] == "mcp")
            {
                return await RunMcpServerAsync();
            }

            // Standard CLI mode
            return await RunCliAsync(args);
        }
        catch (Exception ex)
        {
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
            return await rootCommand.Parse(args).InvokeAsync();
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

    private static async Task<int> RunMcpServerAsync()
    {
        AnsiConsole.MarkupLine("[cyan]Starting MCP server (stdio mode)...[/]");
        AnsiConsole.MarkupLine("[dim]Listening for JSON-RPC requests on stdin[/]");
        
        var server = new McpServer();
        await server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
        
        return 0;
    }

    private static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("MrWhoOidc CLI - Manage your OIDC server");

        // Global options
        var profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Configuration profile to use"
        };
        
        var serverOption = new Option<string?>("--server", "-s")
        {
            Description = "Server URL (overrides profile)"
        };
        
        var formatOption = new Option<OutputFormat>("--format", "-f")
        {
            Description = "Output format (table, json, yaml)",
            DefaultValueFactory = _ => OutputFormat.Table
        };
        
        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose output"
        };

        rootCommand.Options.Add(profileOption);
        rootCommand.Options.Add(serverOption);
        rootCommand.Options.Add(formatOption);
        rootCommand.Options.Add(verboseOption);

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
        
        return rootCommand;
    }
}

public enum OutputFormat
{
    Table,
    Json,
    Yaml
}
