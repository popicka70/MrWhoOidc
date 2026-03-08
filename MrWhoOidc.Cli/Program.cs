using System.CommandLine;
using MrWhoOidc.Cli.Commands;
using MrWhoOidc.Cli.Configuration;
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
        return await rootCommand.InvokeAsync(args);
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
        var rootCommand = new RootCommand("MrWhoOidc CLI - Manage your OIDC server")
        {
            Name = "mrwho-cli"
        };

        // Global options
        var profileOption = new Option<string?>(
            aliases: new[] { "--profile", "-p" },
            description: "Configuration profile to use");
        
        var serverOption = new Option<string?>(
            aliases: new[] { "--server", "-s" },
            description: "Server URL (overrides profile)");
        
        var formatOption = new Option<OutputFormat>(
            aliases: new[] { "--format", "-f" },
            getDefaultValue: () => OutputFormat.Table,
            description: "Output format (table, json, yaml)");
        
        var verboseOption = new Option<bool>(
            aliases: new[] { "--verbose", "-v" },
            description: "Enable verbose output");

        rootCommand.AddGlobalOption(profileOption);
        rootCommand.AddGlobalOption(serverOption);
        rootCommand.AddGlobalOption(formatOption);
        rootCommand.AddGlobalOption(verboseOption);

        // Add command groups (will be implemented in phases)
        rootCommand.AddCommand(new LoginCommand());
        rootCommand.AddCommand(new LogoutCommand());
        rootCommand.AddCommand(new ProfileCommand());
        
        // Placeholder for upcoming commands
        AnsiConsole.MarkupLine("[dim]Additional commands (client, user, tenant, etc.) will be added in next phases[/]");

        return rootCommand;
    }
}

public enum OutputFormat
{
    Table,
    Json,
    Yaml
}
