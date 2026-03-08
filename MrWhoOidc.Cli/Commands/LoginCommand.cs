using System.CommandLine;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Login command - authenticates user via device code flow and stores tokens.
/// Placeholder implementation - will be completed in Phase 2.
/// </summary>
public sealed class LoginCommand : Command
{
    public LoginCommand() : base("login", "Authenticate with the OIDC server")
    {
        var serverOption = new Option<string>(
            aliases: new[] { "--server", "-s" },
            description: "OIDC server URL (e.g., https://auth.example.com)");
        
        var clientIdOption = new Option<string>(
            aliases: new[] { "--client-id", "-c" },
            description: "Client ID for the CLI");

        AddOption(serverOption);
        AddOption(clientIdOption);

        this.SetHandler(async (server, clientId) =>
        {
            await HandleAsync(server, clientId);
        }, serverOption, clientIdOption);
    }

    private static async Task HandleAsync(string? server, string? clientId)
    {
        AnsiConsole.MarkupLine("[yellow]Login command - Phase 2 implementation pending[/]");
        AnsiConsole.MarkupLine($"Server: {server ?? "(not specified)"}");
        AnsiConsole.MarkupLine($"Client ID: {clientId ?? "(not specified)"}");
        AnsiConsole.MarkupLine("[dim]Device code flow will be implemented in Phase 2[/]");
        
        await Task.CompletedTask;
    }
}
