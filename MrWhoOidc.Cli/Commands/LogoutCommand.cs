using System.CommandLine;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Logout command - clears stored authentication tokens.
/// Placeholder implementation - will be completed in Phase 2.
/// </summary>
public sealed class LogoutCommand : Command
{
    public LogoutCommand() : base("logout", "Clear authentication tokens")
    {
        this.SetAction(_ => HandleAsync());
    }

    private static async Task HandleAsync()
    {
        AnsiConsole.MarkupLine("[yellow]Logout command - Phase 2 implementation pending[/]");
        AnsiConsole.MarkupLine("[dim]Token clearing will be implemented in Phase 2[/]");
        
        await Task.CompletedTask;
    }
}
