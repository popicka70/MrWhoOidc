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
        var serverOption = new Option<string?>("--server", "-s")
        {
            Description = "OIDC server URL (e.g., https://auth.example.com)"
        };
        
        var clientIdOption = new Option<string?>("--client-id", "-c")
        {
            Description = "Client ID for the CLI"
        };

        Options.Add(serverOption);
        Options.Add(clientIdOption);

        this.SetAction(async parseResult =>
        {
            var server = parseResult.GetValue(serverOption);
            var clientId = parseResult.GetValue(clientIdOption);
            await HandleAsync(server, clientId);
        });
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
