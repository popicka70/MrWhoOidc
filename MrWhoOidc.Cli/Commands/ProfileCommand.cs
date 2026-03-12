using System.CommandLine;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Profile management commands - list, create, switch, delete profiles.
/// Placeholder implementation - will be completed in Phase 8.
/// </summary>
public sealed class ProfileCommand : Command
{
    public ProfileCommand() : base("profile", "Manage configuration profiles")
    {
        var listCommand = new Command("list", "List all profiles");
        listCommand.SetAction(_ => ListProfilesAsync());
        Subcommands.Add(listCommand);
        
        var switchCommand = new Command("switch", "Switch to a different profile");
        var nameArgument = new Argument<string>("name")
        {
            Description = "Profile name to switch to"
        };
        switchCommand.Arguments.Add(nameArgument);
        switchCommand.SetAction(async parseResult =>
        {
            var name = parseResult.GetValue(nameArgument);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("The profile name argument is required.");
            }

            await SwitchProfileAsync(name);
        });
        Subcommands.Add(switchCommand);
    }

    private static async Task ListProfilesAsync()
    {
        AnsiConsole.MarkupLine("[yellow]Profile list - Phase 8 implementation pending[/]");
        await Task.CompletedTask;
    }

    private static async Task SwitchProfileAsync(string name)
    {
        AnsiConsole.MarkupLine($"[yellow]Profile switch to '{name}' - Phase 8 implementation pending[/]");
        await Task.CompletedTask;
    }
}
